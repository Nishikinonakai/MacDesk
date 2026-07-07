using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MacDesk;

public partial class App : Application
{
    private const string QuitEventName = "MacDesk.QuitEvent";
    private static Mutex? _instanceMutex;
    private EventWaitHandle? _quitEvent;

    /// <summary>用户主动退出（Ctrl+Alt+Q / 菜单 / --quit）。true 时退出才还原原生图标、看门狗才停手。
    /// 分辨率变化、崩溃、被 shell 带走等非用户退出保持 false，由看门狗重新拉起。</summary>
    public static bool UserQuitting { get; private set; }

    /// <summary>发起用户级退出：置 CleanQuit 让看门狗停手，再关掉本进程。</summary>
    public static void BeginUserQuit()
    {
        UserQuitting = true;
        Services.Watchdog.SignalCleanQuit();
        Current.Dispatcher.BeginInvoke(() => Current.Shutdown());
    }

    public static bool HideNativeIcons { get; private set; }
    public static bool Transparent { get; private set; }
    public static bool NoChildStyle { get; private set; }
    public static string ParentMode { get; private set; } = "defview";

    /// <summary>本实例是被恢复逻辑（自我重启/Explorer 重启接管）拉起的：挂载失败时安静退出不弹框。</summary>
    public static bool LaunchedByRecovery { get; private set; }

    /// <summary>本实例是分辨率交接的接管方（--handoff &lt;oldPid&gt;）：老进程还活着撑画面，
    /// 我们挂载就绪后发 Ready 让它退休，老看门狗退场后再拉起自己的。</summary>
    public static bool HandoffTakeover { get; private set; }
    private static int _handoffOldPid;

    /// <summary>交接种子（老进程的图标位置），启动时读一次；非交接启动为 null。</summary>
    internal static Dictionary<string, Services.Handoff.MonitorSeed>? HandoffSeed { get; private set; }

    /// <summary>老进程侧：交接前释放单实例互斥体，让替身能正常拿到。UI 线程调用（与获取同线程）。</summary>
    public static void ReleaseInstanceMutexForHandoff()
    {
        try { _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); } catch { }
        _instanceMutex = null;
    }

    /// <summary>接管方：全部窗口挂载+首排完成 → 通知老进程退休（幂等，多窗口都会调）。</summary>
    public static void NotifyHandoffReadyIfComplete()
    {
        if (!HandoffTakeover) return;
        if (!Desktop.Windows.All(w => w.Attached)) return;
        Services.Handoff.SignalReady();
        HandoffTakeover = false; // 只发一次；后续显示变化按常规主进程处理
        // 老进程退场（其看门狗随 CleanQuit 退休）后，拉起自己的看门狗
        int oldPid = _handoffOldPid;
        new Thread(() =>
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(20000); } catch { }
            Services.MenuHost.EnsureSpawned(); // 老 host 已随老进程退场，管道不再冲突
            for (int i = 0; i < 30 && !Services.Watchdog.EnsureRunning(LaunchModeArgs); i++)
                Thread.Sleep(500); // 老看门狗尚在（CleanQuit 传播中）→ 轮询到它退场
        }) { IsBackground = true }.Start();
    }

    /// <summary>启动时的持久模式开关（自我重启/开机自启复现用户选的模式；排除 --quit 等一次性动作）。</summary>
    public static string[] LaunchModeArgs { get; private set; } = Array.Empty<string>();

    private static string[] ExtractModeArgs(string[] args)
    {
        var mode = new List<string>();
        foreach (var a in args)
            if (a is "--hide-native" or "--transparent" or "--no-child" or "--soft" || a.StartsWith("--parent="))
                mode.Add(a);
        return mode.ToArray();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        HideNativeIcons = e.Args.Contains("--hide-native");
        Transparent = e.Args.Contains("--transparent"); // 实验：WPF 分层子窗口大概率不渲染
        NoChildStyle = e.Args.Contains("--no-child");
        ParentMode = e.Args.FirstOrDefault(a => a.StartsWith("--parent="))?.Substring(9) ?? "defview";
        LaunchedByRecovery = e.Args.Contains("--recovered");
        int ho = Array.IndexOf(e.Args, "--handoff");
        if (ho >= 0 && ho + 1 < e.Args.Length && int.TryParse(e.Args[ho + 1], out _handoffOldPid))
        {
            HandoffTakeover = true;
            LaunchedByRecovery = true; // 挂载失败时安静退出别弹框（老进程还在撑着）
            HandoffSeed = Services.Handoff.TryLoadSeed();
        }
        LaunchModeArgs = ExtractModeArgs(e.Args);
        if (e.Args.Contains("--soft"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 开机自启开关（一次性动作，设置完即退）：可带模式开关，如 --enable-autostart --hide-native
        if (e.Args.Contains("--enable-autostart"))
        {
            Services.Autostart.Enable(LaunchModeArgs);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--disable-autostart"))
        {
            Services.Autostart.Disable();
            Shutdown();
            return;
        }

        // 看门狗子进程：无窗口，盯着主进程，主进程非正常消失就重新拉起（Explorer 重启恢复的核心）
        // --watchdog <mainPid> [modeArgs]
        int wd = Array.IndexOf(e.Args, "--watchdog");
        if (wd >= 0 && wd + 1 < e.Args.Length && int.TryParse(e.Args[wd + 1], out int mainPid))
        {
            new Thread(() => { Services.Watchdog.Run(mainPid, LaunchModeArgs); Dispatcher.BeginInvoke(() => Shutdown()); })
                { IsBackground = true }.Start();
            return; // 保持进程存活，看门狗循环在后台线程里跑
        }

        // 常驻右键菜单 host：预热 shell 扩展，命名管道收请求（隔离性同旧一次性子进程）
        // --menuhost <mainPid>
        int mh = Array.IndexOf(e.Args, "--menuhost");
        if (mh >= 0 && mh + 1 < e.Args.Length && int.TryParse(e.Args[mh + 1], out int menuParent))
        {
            var t = new Thread(() => Services.MenuHost.Run(menuParent)) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // shell COM + TrackPopupMenu 需要 STA
            t.Start();
            return; // 进程存活，host 循环在专用线程里跑
        }

        // 菜单结构侦察：构建 bg + 指定文件菜单（不显示），结构树写日志
        // --menudump [path ...]
        int md = Array.IndexOf(e.Args, "--menudump");
        if (md >= 0)
        {
            var dmw = new MessageWindow(registerHotkey: false);
            Services.ShellContextMenu.DumpMenus(e.Args.Skip(md + 1).ToArray(), dmw.Handle);
            dmw.Dispose();
            Environment.Exit(0);
            return;
        }

        // 菜单安全性探针（牺牲进程）：只 QueryContextMenu 不显示，崩了就崩（host 看退出码）
        // --menuprobe <path>
        int mp = Array.IndexOf(e.Args, "--menuprobe");
        if (mp >= 0 && mp + 1 < e.Args.Length)
        {
            var mw = new MessageWindow(registerHotkey: false);
            bool ok = Services.ShellContextMenu.ProbeFile(e.Args[mp + 1], mw.Handle);
            mw.Dispose();
            Environment.Exit(ok ? 0 : 2);
            return;
        }

        // 一次性右键菜单子进程（host 不可用时的兜底路径）
        // --contextmenu <x> <y> <path> [path2 ...]
        int cm = Array.IndexOf(e.Args, "--contextmenu");
        if (cm >= 0 && e.Args.Length >= cm + 4)
        {
            var mw = new MessageWindow(registerHotkey: false);
            Services.ShellContextMenu.Show(e.Args.Skip(cm + 3).ToArray(), mw.Handle,
                int.Parse(e.Args[cm + 1]), int.Parse(e.Args[cm + 2]));
            mw.Dispose();
            Shutdown();
            return;
        }

        // --bgmenu <x> <y> <folder>：桌面背景菜单（新建/粘贴/… + 自定义项）
        int bg = Array.IndexOf(e.Args, "--bgmenu");
        if (bg >= 0 && e.Args.Length >= bg + 4)
        {
            var mw = new MessageWindow(registerHotkey: false);
            Services.ShellContextMenu.ShowBackground(e.Args[bg + 3], mw.Handle,
                int.Parse(e.Args[bg + 1]), int.Parse(e.Args[bg + 2]));
            mw.Dispose();
            Shutdown();
            return;
        }

        Services.Log.Write($"startup args=[{string.Join(" ", e.Args)}]");
        AppDomain.CurrentDomain.UnhandledException += (_, ue) =>
            Services.Log.Write($"FATAL: {ue.ExceptionObject}");
        DispatcherUnhandledException += (_, de) =>
        {
            Services.Log.Write($"dispatcher exception (handled): {de.Exception}");
            de.Handled = true; // MVP：记日志继续跑，别把桌面层崩掉
        };

        if (e.Args.Contains("--quit"))
        {
            // 远程/命令行退出已运行实例
            try
            {
                using var evt = EventWaitHandle.OpenExisting(QuitEventName);
                evt.Set();
            }
            catch { /* 没有运行中的实例 */ }
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, "MacDesk.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 恢复/交接拉起的实例撞上活体 = 竞态残留，安静退让别弹框（用户手动双开才提示）
            if (!LaunchedByRecovery)
                MessageBox.Show("MacDesk 已在运行。用 MacDesk.exe --quit 退出。", "MacDesk");
            Shutdown();
            return;
        }

        _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        var waiter = new Thread(() =>
        {
            _quitEvent.WaitOne();
            BeginUserQuit(); // --quit 也走用户退出：停看门狗、还原原生图标
        }) { IsBackground = true };
        waiter.Start();

        // 拉起看门狗盯着自己（Explorer 重启 / 崩溃后重新接管）。
        // 交接接管方例外：老看门狗还在盯老进程，等 Ready 后由 NotifyHandoffReadyIfComplete 接棒。
        if (!HandoffTakeover) Services.Watchdog.EnsureRunning(LaunchModeArgs);

        // 预热常驻菜单 host：shell 扩展 DLL 现在加载，右键菜单零冷启动。
        // 交接接管方推迟到老进程退场后（NotifyHandoffReadyIfComplete）——两个 host 抢同名
        // 管道会刷 "All pipe instances are busy" 且探针可能在混战中误判降级（真机实锤）。
        if (!HandoffTakeover) Services.MenuHost.EnsureSpawned();

        // 菜单深色模式跟随（v2 菜单在本进程弹出才有意义；浅色主题下无可见变化）
        Services.ShellContextMenu.EnableModernMenuTheme();

        base.OnStartup(e);

        // 每显示器一个桌面窗口；主屏窗口是 Application.MainWindow（它关闭 = 整个进程退出）
        Desktop.Init();
        foreach (var m in Desktop.Monitors)
            Desktop.Windows.Add(new MainWindow(m, isPrimary: m == Desktop.Monitors[0]));
        MainWindow = Desktop.Windows[0];
        foreach (var w in Desktop.Windows) w.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 只有用户主动退出才还原原生图标；非用户退出（分辨率变化/被 shell 带走）交给看门狗拉起的新实例
        if (UserQuitting) Interop.DesktopLayer.SetNativeIconsVisible(true);
        _instanceMutex?.Dispose();
        _quitEvent?.Dispose();
        base.OnExit(e);
    }
}
