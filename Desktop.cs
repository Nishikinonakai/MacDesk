using System.Windows.Threading;
using MacDesk.Interop;
using MacDesk.Services;

namespace MacDesk;

/// <summary>
/// 多显示器协调器：共享服务（文件枚举/布局档/设置）+ 每显示器一个 MainWindow。
/// 图标归属规则（macOS 语义）：
///  - 归属显示器在场 → 在那台显示器的窗口显示；
///  - 归属显示器不在场 → 主屏窗口**现场推导**显示（归属与坐标不改写，重新接上即回归）；
///  - 从未见过的新图标 → 主屏，由其 LayoutAll 分配归属。
/// 所有窗口同一 UI 线程。
/// </summary>
internal static class Desktop
{
    public static List<MonitorInfo> Monitors { get; private set; } = new();
    public static List<MainWindow> Windows { get; } = new();
    public static DesktopItemProvider Provider { get; private set; } = null!;
    public static LayoutStore Layout { get; private set; } = null!;
    public static Settings Config { get; private set; } = null!;
    public static string PrimaryKey => Monitors[0].Key;

    private static DispatcherTimer? _fsDebounce;
    private static readonly HashSet<string> _attachedKeys = new();

    public static void Init()
    {
        // 显示变化后看门狗几百毫秒就把我们拉起来，拓扑可能还在洗牌（适配器→EDID 映射会给旧值）。
        // 等两次枚举结果一致再继续（每轮 400ms，最多 ~4s）。
        Monitors = Interop.Monitors.GetAll();
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(400);
            var again = Interop.Monitors.GetAll();
            bool same = again.Count == Monitors.Count && again.Zip(Monitors).All(p =>
                p.First.Key == p.Second.Key &&
                p.First.Physical.Left == p.Second.Physical.Left &&
                p.First.Physical.Top == p.Second.Physical.Top &&
                p.First.Physical.Right == p.Second.Physical.Right &&
                p.First.Physical.Bottom == p.Second.Physical.Bottom &&
                p.First.Dpi == p.Second.Dpi);
            Monitors = again;
            if (same) break;
            Log.Write("monitor topology still settling, re-enumerating...");
        }
        foreach (var m in Monitors) _attachedKeys.Add(m.Key);
        Log.Write($"monitors: {string.Join(" | ", Monitors.Select(m => $"{m.Key}{(m.IsPrimary ? "*" : "")} {m.Device} ({m.Physical.Left},{m.Physical.Top} {m.Physical.Width}x{m.Physical.Height} dpi={m.Dpi})"))}");

        Layout = new LayoutStore(PrimaryKey);
        Layout.DailyBackup(); // 启动即落当日滚动备份（每小时再查，跨零点长开机也有份）
        var backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        backupTimer.Tick += (_, _) => Layout.DailyBackup();
        backupTimer.Start();
        Config = Settings.Load();
        Provider = new DesktopItemProvider();

        // OOBE 首启：布局档为空（新装）时询问是否导入原生桌面的现有摆放
        if (Layout.IsEmpty)
        {
            try
            {
                if (Interop.DesktopLayer.EnsureDiscovered())
                {
                    var native = Interop.NativeDesktopLayout.Read();
                    if (native.Count > 0 && System.Windows.MessageBox.Show(
                            L.T($"欢迎使用 MacDesk！\n\n检测到桌面上已有 {native.Count} 个图标。要保留它们现在的摆放吗？\n\n" +
                                "选择“是”导入现有布局；选择“否”将从整洁的 mac 式右上排列开始。",
                                $"Welcome to MacDesk!\n\nFound {native.Count} icons on your desktop. Keep their current arrangement?\n\n" +
                                "Choose Yes to import the existing layout, or No to start with a clean mac-style top-right flow."),
                            L.T("MacDesk 首次启动", "MacDesk First Run"),
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
                        Interop.NativeDesktopLayout.Import(native, Monitors, Layout, Provider.Enumerate());
                }
            }
            catch (Exception ex) { Log.Write("OOBE import failed: " + ex.Message); }
        }
        _fsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fsDebounce.Tick += (_, _) => { _fsDebounce.Stop(); RefreshAll(); };
        Provider.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _fsDebounce.Stop();
            _fsDebounce.Start();
        });

        // 壁纸变化跟随，双通道：
        // ①事件快路径（SystemEvents 包装 WM_SETTINGCHANGE）——SPI 广播型换壁纸秒级跟；
        // ②8s 轮询兜底——设置应用走 IDesktopWallpaper::SetWallpaper **不广播**（机主实测
        //   事件路径漏跟），轮询里签名比对无变化零开销，变了才重渲染。
        var wpDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        wpDebounce.Tick += (_, _) =>
        {
            wpDebounce.Stop();
            foreach (var w in Windows) w.ApplyDesktopBackground();
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop
                           or Microsoft.Win32.UserPreferenceCategory.General)
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    wpDebounce.Stop();
                    wpDebounce.Start();
                });
        };
        var wpPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        wpPoll.Tick += (_, _) =>
        {
            foreach (var w in Windows)
            {
                w.ApplyDesktopBackground();
                w.ApplyWallpaperMode(); // WE 启动/退出检测（动态模式内的健康检查走各窗心跳）
            }
        };
        wpPoll.Start();

        // 强调色切换 → 全窗口刷新选中态视觉
        Accent.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var w in Windows) w.RefreshAccent();
        });
    }

    /// <summary>图标的有效规范位置（无视归属显示器是否在场）。</summary>
    public static CanonPos? EffectiveCanon(string name)
    {
        var owner = Layout.FindOwner(name);
        return owner == null ? null : Layout.Get(owner, name);
    }

    /// <summary>该图标应该显示在哪个窗口：归属在场给归属，否则主屏。</summary>
    private static string EffectiveWindowKey(string name)
    {
        var owner = Layout.FindOwner(name);
        return owner != null && _attachedKeys.Contains(owner) ? owner : PrimaryKey;
    }

    /// <summary>全量刷新：枚举一次，按归属分发给各窗口。"导入布局"记名的 missing 项
    /// 一并分发（macOS 式问号占位）；文件回来了自动除名让位真图标。</summary>
    public static void RefreshAll()
    {
        var all = Provider.Enumerate();
        // 文件夹堆叠标记剪枝：目标文件夹没了（删除/外部改名）标记随之失效，settings.json 别积尸；
        // 顺带只认桌面根上的文件夹——手改配置塞进来的任意路径（如桌面根自身）会搅乱拖拽语义。
        // 外部改名丢标记是接受的取舍（watcher 的 Renamed 被折叠成无参 Changed，分不清改名与删+建）。
        if (Config.StackFolders.Count > 0)
        {
            static bool OnDesktopRoot(string p)
            {
                var d = System.IO.Path.GetDirectoryName(p);
                return string.Equals(d, DesktopItemProvider.UserDesktop, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d, DesktopItemProvider.PublicDesktop, StringComparison.OrdinalIgnoreCase);
            }
            if (Config.StackFolders.RemoveAll(p => !System.IO.Directory.Exists(p) || !OnDesktopRoot(p)) > 0)
                Config.Save();
        }
        var present = new HashSet<string>(
            all.Select(en => System.IO.Path.GetFileName(en.Path)), StringComparer.OrdinalIgnoreCase);
        var returned = Layout.MissingListed.Where(present.Contains).ToList();
        if (returned.Count > 0)
        {
            foreach (var n in returned) Layout.UnlistMissing(n);
            Layout.Save();
        }
        var missing = Layout.MissingListed.Where(n => !present.Contains(n)).ToList();
        foreach (var w in Windows)
        {
            if (!w.Attached) continue;
            var subset = all.Where(en =>
                EffectiveWindowKey(System.IO.Path.GetFileName(en.Path)) == w.Monitor.Key).ToList();
            var missSubset = missing.Where(n => EffectiveWindowKey(n) == w.Monitor.Key).ToList();
            w.RefreshItems(subset, missSubset);
        }
    }

    /// <summary>"导入布局"完成后调用：当时缺文件的条目记入问号名单（只有这个动作会记名，
    /// 历史遗留孤儿保持隐形），然后全量刷新重排。</summary>
    public static void OnLayoutImported()
    {
        var present = new HashSet<string>(
            Provider.Enumerate().Select(en => System.IO.Path.GetFileName(en.Path)), StringComparer.OrdinalIgnoreCase);
        Layout.SetMissingList(Layout.AllNames().Where(n => !present.Contains(n)));
        RefreshAll();
        LayoutAllWindows(animated: true);
    }

    public static void LayoutAllWindows(bool animated)
    {
        foreach (var w in Windows) if (w.Attached) w.LayoutAll(animated);
    }

    /// <summary>图标尺寸档（base 图标 DIU），Ctrl +/- 用；64=默认。小端加密（32/40/48）——
    /// 4K 高 DPI（如 4K@300% 逻辑 1280×720）下 64 显得傻大，用户需要更小档（实测 40 舒适、
    /// 32 紧凑），故档位下探到 32、小端更细。滑杆无极可达任意值，此表仅供快捷键逐档。</summary>
    public static readonly int[] IconSizeSteps = { 32, 40, 48, 64, 80, 96, 112, 128 };
    public const int DefaultIconSize = 64;

    /// <summary>Ctrl +/-：按档位增减图标大小（与 Finder 一致）。取当前值最近的档，移动 delta。</summary>
    public static void StepIconSize(int delta)
    {
        int cur = Config.IconSize, idx = 0, best = int.MaxValue;
        for (int i = 0; i < IconSizeSteps.Length; i++)
        {
            int d = Math.Abs(IconSizeSteps[i] - cur);
            if (d < best) { best = d; idx = i; }
        }
        idx = Math.Clamp(idx + delta, 0, IconSizeSteps.Length - 1);
        SetIconSize(IconSizeSteps[idx]);
    }

    /// <summary>设图标尺寸并全量重建（经现有工厂在新档下重建，红线不破：只重算显示，不写 Canon）。
    /// 先 teardown 全部视觉是因为 RefreshItems 只为新路径建图标——不清空就不会按新尺寸重建。</summary>
    public static void SetIconSize(int size)
    {
        size = Math.Clamp(size, 32, 160);
        if (Config.IconSize == size) return;
        Config.IconSize = size;
        Config.Save();
        foreach (var w in Windows) if (w.Attached) w.TearDownVisuals();
        RefreshAll();
        LayoutAllWindows(animated: true);
    }

}
