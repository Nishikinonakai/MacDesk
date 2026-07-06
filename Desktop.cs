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
        Config = Settings.Load();
        Provider = new DesktopItemProvider();
        _fsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fsDebounce.Tick += (_, _) => { _fsDebounce.Stop(); RefreshAll(); };
        Provider.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _fsDebounce.Stop();
            _fsDebounce.Start();
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

    /// <summary>全量刷新：枚举一次，按归属分发给各窗口。</summary>
    public static void RefreshAll()
    {
        var all = Provider.Enumerate();
        foreach (var w in Windows)
        {
            if (!w.Attached) continue;
            var subset = all.Where(en =>
                EffectiveWindowKey(System.IO.Path.GetFileName(en.Path)) == w.Monitor.Key).ToList();
            w.RefreshItems(subset);
        }
    }

    public static void LayoutAllWindows(bool animated)
    {
        foreach (var w in Windows) if (w.Attached) w.LayoutAll(animated);
    }

}
