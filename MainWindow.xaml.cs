using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacDesk.Interop;
using MacDesk.Services;
using Microsoft.Win32;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using Clipboard = System.Windows.Clipboard;
using ToolTip = System.Windows.Controls.ToolTip;
using DragDropEffects = System.Windows.DragDropEffects;
using StringCollection = System.Collections.Specialized.StringCollection;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDrop = System.Windows.DragDrop;
using SystemSounds = System.Media.SystemSounds;
using FontFamily = System.Windows.Media.FontFamily;

namespace MacDesk;

public partial class MainWindow : Window
{
    // mac 式网格（DIU）：base（S=1）= 112×112 方形格（对齐 Finder gridSpacing 实测值）。
    // 图标尺寸档只换缩放因子 S = IconSize/64（base 图标 64 DIU）；格/间距/字号/图源全随 S 现算，
    // 重排现场推导显示位置、**绝不回写 Canon**（分辨率无关红线，切档同切分辨率一个道理）。
    // 改档走 Desktop.SetIconSize → RebuildForScale（经现有工厂重建，见那里）。
    private double S => Math.Clamp(Config.IconSize, 32, 160) / 64.0;
    private double CellW => 96 * S;
    private double CellH => 104 * S;
    private double GapX => 16 * S;
    private double GapY => 8 * S;

    private static readonly FontFamily LabelFontFamily = new("Segoe UI, Microsoft YaHei UI");
    private const double MarginTop = 14, MarginRight = 14, MarginBottom = 60, MarginLeft = 14;

    /// <summary>取图尺寸 = 图标 DIU(64·S) × 本屏 DPI，现算：大档 + 高 DPI（4K@300% 96×3=288）
    /// 会超旧的 256 常量致糊。下限 256（小档也留高清余量）、上限 512（shell 少有更大真源），
    /// 按 64 取整界定 IconLoader 的 (ext,size) 缓存代数。_dpiK 是本窗口的，故 IconPx 是实例 getter。</summary>
    private int IconPx
    {
        get
        {
            int px = (int)Math.Ceiling(64 * S * _dpiK);
            px = (px + 63) / 64 * 64; // 向上取整到 64 的倍数
            return Math.Clamp(px, 256, 512);
        }
    }

    /// <summary>本窗口负责的显示器（多显示器：每屏一个 MainWindow，见 Desktop 协调器）。</summary>
    internal MonitorInfo Monitor { get; }
    internal bool IsPrimary { get; }
    internal IntPtr Hwnd => _hwnd;
    internal bool Attached => _attached;

    private string MonKey => Monitor.Key;
    private static LayoutStore LayoutFile => Desktop.Layout;
    private static Services.Settings Config => Desktop.Config;

    private string? _arrangeOrder; // 一次性排序整理时的排序键（name/date/size/kind）
    private readonly Dictionary<string, IconVisual> _icons = new(); // key: full path
    private MessageWindow? _msgWin;
    private IntPtr _hwnd;
    private readonly DispatcherTimer? _displayDebounce;
    private readonly HashSet<IconVisual> _selection = new();

    // 框选
    private bool _bandActive;
    private Point _bandOrigin;
    private System.Windows.Shapes.Rectangle? _bandRect;

    // 重命名
    private IconVisual? _renaming;
    private TextBox? _renameBox;

    // 键盘导航焦点 + 首字母定位
    private IconVisual? _focusIcon;
    private string _typeAhead = "";
    private DateTime _typeAheadAt = DateTime.MinValue;

    // 空格预览是否已打开（方向键据此决定要不要让预览跟随；Esc/清选归零，见 FilePreview）
    private bool _previewOpen;

    /// <summary>空格预览的目标：焦点图标（在选中集内时）否则任一选中项；虚拟项/未选中返回 null。</summary>
    private IconVisual? FocusItem =>
        (_focusIcon != null && _selection.Contains(_focusIcon)) ? _focusIcon : _selection.FirstOrDefault();

    private string? FocusPreviewPath =>
        FocusItem is { } iv && !iv.Entry.Path.StartsWith("::") ? iv.Entry.Path : null;

    private sealed class IconVisual
    {
        public required DesktopEntry Entry;
        public required Border Root;
        public required Border IconPlate;
        public required Border LabelPlate;
        public required TextBlock Label;
        public CanonPos? Canon;
        // 拖拽状态
        public bool MouseDown, Dragging;
        public Point DownPos; // 相对 canvas
        // 文件夹堆叠（见 "── 文件夹堆叠 ──" 区）
        public bool IsDir;                   // 建档快照：分类与图标包装恒一致（junction 目标死了
                                             // 也不跳去饼堆——否则 scrub 强转 Grid 会崩）
        public bool StackChild;              // 展开的文件夹堆叠里的临时子项：不进 _icons、不碰 Canon/布局档
        public Border? StackBadge;           // 堆叠文件夹角标（挂在 IconPlate 的 Grid 包装里）
        public RotateTransform? StackChevronRot; // 角标箭头：展开时翻转朝上
    }

    internal MainWindow(MonitorInfo monitor, bool isPrimary)
    {
        Monitor = monitor;
        IsPrimary = isPrimary;
        if (App.Transparent)
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }
        InitializeComponent();
        // 布局取整到像素边界：StackPanel 居中会把标签排到 .5 偏移上，
        // 奇偶宽度不同的标签一半清晰一半糊（机主红圈截图实锤）
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        if (!App.Transparent)
        {
            Background = Brushes.Black;
            ApplyDesktopBackground(); // 壁纸镜像：按本显示器画系统壁纸当底，变化时跟随
        }
        // 先在屏幕外完整渲染，首帧后再挂到桌面层（见 OnContentRendered）
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -8000 - Desktop.Windows.Count * 2100; // 各窗口错开，别叠在一起渲染
        Top = 0;
        Width = 1280;
        Height = 800;
        if (IsPrimary)
        {
            _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _displayDebounce.Tick += (_, _) => { _displayDebounce.Stop(); OnDisplayChangedDebounced(); };
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(ForceCoverHook);

        // 空白处：左键框选 / 右键背景菜单
        RootGrid.MouseLeftButtonDown += OnCanvasMouseDown;
        RootGrid.MouseMove += OnCanvasMouseMove;
        RootGrid.MouseLeftButtonUp += OnCanvasMouseUp;
        RootGrid.MouseRightButtonUp += OnCanvasRightClick;
        // 菜单进程抢前台会打断我们的鼠标捕获，此后 MouseLeftButtonUp 永远不来——
        // 不接这个事件，框选框就"死"在桌面上且 RootGrid 持续劫持后续输入（真机踩坑）
        RootGrid.LostMouseCapture += (_, _) => EndBand();
        PreviewKeyDown += OnKeyDown;
        TextInput += OnTextInput; // 首字母定位
        PreviewMouseRightButtonDown += (_, e) =>
        {
            var p = e.GetPosition(IconCanvas);
            _rightPress = (p, IconAtPoint(p), DateTime.Now);
        };
        // 桌面本体禁用 IME：字母键直接走首字母定位，别被中文输入法拦成拼音候选（重命名框会单独放开）
        InputMethod.SetIsInputMethodEnabled(this, false);
        DragEnter += OnDesktopDragEnter;
        DragOver += OnDesktopDragOver;
        DragLeave += OnDesktopDragLeave;
        Drop += OnDesktopDrop;

        if (!IsPrimary) return;

        // ── 以下共享职责只挂主屏窗口：显示变化、热键、菜单命令（作用于所有窗口） ──
        _msgWin = new MessageWindow();
        _msgWin.DisplayChanged += (_, _) => Dispatcher.BeginInvoke(() => { _displayDebounce!.Stop(); _displayDebounce.Start(); });
        _msgWin.QuitRequested += () => Dispatcher.BeginInvoke(App.BeginUserQuit);
        // 双保险：隐藏窗口收不到广播时靠 SystemEvents
        SystemEvents.DisplaySettingsChanged += OnSystemDisplayChanged;

        CommandChannel.Listen("Arrange", () => Dispatcher.BeginInvoke(ArrangeAllWithUndo));
        CommandChannel.Listen("Undo", () => Dispatcher.BeginInvoke(UndoArrange));
        CommandChannel.Listen("ToggleNative", () => Dispatcher.BeginInvoke(() =>
            DesktopLayer.SetNativeIconsVisible(!DesktopLayer.NativeIconsVisible)));
        CommandChannel.Listen("ToggleAutostart", () => Dispatcher.BeginInvoke(() =>
            Autostart.Toggle(App.LaunchModeArgs)));
        CommandChannel.Listen("ToggleFree", () => Dispatcher.BeginInvoke(ToggleFreePlacement));
        CommandChannel.Listen("SortName", () => Dispatcher.BeginInvoke(() => SortArrangeAll("name")));
        CommandChannel.Listen("SortDate", () => Dispatcher.BeginInvoke(() => SortArrangeAll("date")));
        CommandChannel.Listen("SortSize", () => Dispatcher.BeginInvoke(() => SortArrangeAll("size")));
        CommandChannel.Listen("SortKind", () => Dispatcher.BeginInvoke(() => SortArrangeAll("kind")));
        CommandChannel.Listen("Quit", () => Dispatcher.BeginInvoke(App.BeginUserQuit));
        CommandChannel.Listen("OpenSettings", () => Dispatcher.BeginInvoke(SettingsWindow.ShowSingleton));
        CommandChannel.Listen("ToggleStacks", () => Dispatcher.BeginInvoke(() =>
        {
            Config.UseStacks = !Config.UseStacks;
            Config.Save();
            Desktop.LayoutAllWindows(animated: true);
        }));
        CommandChannel.Listen("GroupKind", () => Dispatcher.BeginInvoke(() => SetStackGroupBy("kind")));
        CommandChannel.Listen("GroupDate", () => Dispatcher.BeginInvoke(() => SetStackGroupBy("date")));
        CommandChannel.Listen("GroupSize", () => Dispatcher.BeginInvoke(() => SetStackGroupBy("size")));

        // 降级文件菜单（原生菜单被崩溃扩展拖垮时）的核心动词：对当前选中执行
        CommandChannel.Listen("OpenSelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.OpenSelectionItems()));
        CommandChannel.Listen("CutSelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.ClipboardCopyCut(cut: true)));
        CommandChannel.Listen("CopySelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.ClipboardCopyCut(cut: false)));
        CommandChannel.Listen("DeleteSelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.DeleteSelection()));
        CommandChannel.Listen("RenameSelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.RenameFirstSelected()));
        CommandChannel.Listen("PropertiesSelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.ShowSelectionProperties()));
        CommandChannel.Listen("NewFolderWithSelection", () => Dispatcher.BeginInvoke(() => SelectionWindow()?.CreateFolderWithSelection()));
        CommandChannel.Listen("ToggleFolderStack", () => Dispatcher.BeginInvoke(ToggleFolderStackFromSelection));
    }

    /// <summary>当前持有选中项的窗口（菜单动词的作用对象；右键弹菜单前必然已设选中）。</summary>
    private static MainWindow? SelectionWindow() => Desktop.Windows.FirstOrDefault(w => w._selection.Count > 0);

    private void OpenSelectionItems()
    {
        foreach (var s in _selection.ToList()) OpenEntry(s.Entry);
    }

    private void RenameFirstSelected()
    {
        if (_selection.Count == 1) StartRename(_selection.First());
    }

    private void ShowSelectionProperties()
    {
        var sel = _selection.FirstOrDefault();
        if (sel == null) return;
        try { Native.ShowFileProperties(_hwnd, sel.Entry.Path); }
        catch (Exception ex) { Log.Write("properties failed: " + ex.Message); }
    }

    private bool _attached;         // 成功挂载
    private bool _attachStarted;    // 已进入挂载重试链（防 OnContentRendered 重入）
    private int _attachAttempts;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_attachStarted) return;
        _attachStarted = true;
        Log.Write($"[{MonKey}] first frame rendered, attaching (parent={App.ParentMode} child={!App.NoChildStyle})");
        AttemptAttach();
    }

    /// <summary>
    /// 挂载桌面层，失败则按 500ms 节奏重试（最多 ~20s）。
    /// 重试是 Explorer 重启恢复的关键：看门狗在旧 shell 死、新 shell 未起的空窗期把我们拉起时，
    /// 我们会耐心等新 SHELLDLL_DefView 出现，而不是立刻报错退出。
    /// </summary>
    private void AttemptAttach()
    {
        if (_attached) return;
        bool ok = DesktopLayer.EnsureDiscovered(App.ParentMode) &&
                  DesktopLayer.AttachWindow(_hwnd, !App.NoChildStyle);
        if (!ok)
        {
            _attachAttempts++;
            if (_attachAttempts >= 40)
            {
                // 20s 都等不到 shell = 真的没有桌面可挂 → 清洁退出并停掉看门狗（重启也没用）
                Log.Write("attach FAILED after retries; giving up");
                if (!App.LaunchedByRecovery)
                    MessageBox.Show(L.T("挂载桌面层失败（找不到 Progman/DefView）。", "Failed to attach to the desktop layer (Progman/DefView not found)."), "MacDesk");
                App.BeginUserQuit();
                return;
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (_, _) => { t.Stop(); AttemptAttach(); };
            t.Start();
            return;
        }

        _attached = true;
        Log.Write($"[{MonKey}] attached hwnd={_hwnd} parent={DesktopLayer.ParentHwnd} progman={DesktopLayer.ProgmanHwnd} defview={DesktopLayer.DefViewHwnd}");
        CoverAndSync();
        if (IsPrimary && App.HideNativeIcons) DesktopLayer.SetNativeIconsVisible(false);

        Desktop.RefreshAll(); // 已挂载的窗口各取自己的图标子集
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Log.Write($"[{MonKey}] attached+laid out worksize={RootGrid.ActualWidth:F0}x{RootGrid.ActualHeight:F0} icons={_icons.Count}");
            // 分辨率交接：先把图标放到"老进程位置按比例映射"的起点，再动画滑向推导位
            //（macOS 式 morph——分辨率切换不再是跳变，图标原地滑到新家）
            if (App.HandoffSeed != null && App.HandoffSeed.TryGetValue(MonKey, out var seed) &&
                seed.W > 1 && seed.H > 1)
            {
                var (w, h) = WorkSize;
                double kx = w / seed.W, ky = h / seed.H;
                int seeded = 0;
                foreach (var iv in _icons.Values)
                {
                    if (!seed.Icons.TryGetValue(Path.GetFileName(iv.Entry.Path), out var p)) continue;
                    MoveElement(iv.Root, p[0] * kx, p[1] * ky, false, EaseGlide, GlideMs);
                    seeded++;
                }
                Log.Write($"[{MonKey}] handoff morph: {seeded}/{_icons.Count} icons seeded (scale {kx:F2}x{ky:F2})");
                LayoutAll(animated: true);
            }
            else LayoutAll(animated: false);
            App.NotifyHandoffReadyIfComplete();
        });
    }

    private void OnSystemDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _displayDebounce?.Stop(); _displayDebounce?.Start(); });

    protected override void OnClosed(EventArgs e)
    {
        // 呈现层跟窗口同生共死；WE 窗用户退出时还原回 WorkerW（桌面回原生态也有壁纸），
        // 交接退场时留在 DefView 给替身实例无缝再收编
        ExitDynamic(release: !HandoffRetiring);
        if (IsPrimary)
        {
            SystemEvents.DisplaySettingsChanged -= OnSystemDisplayChanged;
            LayoutFile.Save();
            // 只有用户主动退出才还原原生图标；非用户退出（分辨率变化/被 shell 带走）由看门狗拉起的新实例接管
            if (App.UserQuitting) DesktopLayer.SetNativeIconsVisible(true);
            _msgWin?.Dispose();
            Desktop.Provider.Dispose();
        }
        base.OnClosed(e);
    }

    // ── 显示变化 ──────────────────────────────────────────────

    private bool _handoffInProgress;

    /// <summary>显示变化 → 原地平滑交接（活体改尺寸不可救是定案，见下；旧方案 = 立刻自杀等
    /// 看门狗拉新实例，~1s 裸桌面闪屏）。新方案：本进程窗口原地撑住画面，spawn `--handoff`
    /// 替身在新分辨率下走"启动时挂载"可靠路径；替身把图标按种子放到比例映射的旧位置再动画
    /// 滑向推导位（macOS 式 morph），全部就绪发 Ready，本进程才退休。超时 = 回退旧路径。</summary>
    private void OnDisplayChangedDebounced()
    {
        if (!_attached || _handoffInProgress) return;
        _handoffInProgress = true;
        HandoffRetiring = true; // 所有窗口退场时保留 WE 收编现场给替身
        // 重挂子窗口的活体改尺寸在 WPF/DPI 虚拟化下不可靠（多次实测：MoveWindow 后布局尺寸卡旧值）。
        Log.Write("display change -> spawning handoff replacement");
        Services.Watchdog.EnsureRunning(App.LaunchModeArgs); // 兜底：超时路径仍靠它接管

        try
        {
            var seed = new Dictionary<string, Handoff.MonitorSeed>();
            foreach (var w in Desktop.Windows.Where(w => w.Attached)) w.CollectHandoffSeed(seed);
            Handoff.WriteSeed(seed);
        }
        catch (Exception ex) { Log.Write("seed collect failed: " + ex.Message); }

        _msgWin?.Dispose(); _msgWin = null;      // 释放全局热键，替身才能注册成功
        App.ReleaseInstanceMutexForHandoff();     // 替身正常拿单实例锁

        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Handoff.ReadyEventName);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in App.LaunchModeArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--handoff");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            // spawn 都失败 → 老路径：自杀等看门狗
            Log.Write("handoff spawn failed: " + ex.Message);
            ready.Dispose();
            Close();
            return;
        }

        new Thread(() =>
        {
            bool ok = ready.WaitOne(TimeSpan.FromSeconds(15));
            ready.Dispose();
            Log.Write(ok ? "handoff ready -> retiring old instance"
                         : "handoff timeout -> plain exit, watchdog takes over");
            if (ok) Services.Watchdog.SignalCleanQuit(); // 替身会拉起自己的看门狗
            Dispatcher.BeginInvoke(Close);
        }) { IsBackground = true }.Start();
    }

    /// <summary>交接种子：本窗口图标的当前 DIU 坐标 + 工作区尺寸（替身按比例映射做 morph 起点）。</summary>
    internal void CollectHandoffSeed(Dictionary<string, Handoff.MonitorSeed> seed)
    {
        var (w, h) = WorkSize;
        var mon = new Handoff.MonitorSeed { W = w, H = h };
        foreach (var iv in _icons.Values)
        {
            double l = Canvas.GetLeft(iv.Root), t = Canvas.GetTop(iv.Root);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            mon.Icons[Path.GetFileName(iv.Entry.Path)] = new[] { l, t };
        }
        seed[MonKey] = mon;
    }

    private Native.RECT _forceRect; // 本窗口在父客户区坐标里的目标矩形（钩子钳制用）
    private bool _forceRectValid;

    private void CoverAndSync()
    {
        _forceRect = DesktopLayer.CoverRect(_hwnd, Monitor.Physical);
        _forceRectValid = true;

        // 子窗口的 WM_DPICHANGED_AFTERPARENT 在快切分辨率时会丢，WPF 的 DPI 账本会卡在旧值。
        // 不信任它：自己查本显示器真实 DPI，差值用 LayoutTransform 补偿。
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            double believed = 1 / ct.TransformFromDevice.M11;
            var center = new Native.POINT
            {
                X = (Monitor.Physical.Left + Monitor.Physical.Right) / 2,
                Y = (Monitor.Physical.Top + Monitor.Physical.Bottom) / 2,
            };
            double actual = believed;
            var mon = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTOPRIMARY);
            if (Native.GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0)
                actual = dx / 96.0;

            double k = actual / believed;
            bool dpiChanged = Math.Abs(_dpiK - actual) > 0.01;
            _dpiK = actual; // BitmapCache 的 RenderAtScale 用（高 DPI 屏缓存要按物理倍率烙）
            if (dpiChanged) ApplyCacheModeAll(); // 已建元素的缓存按新倍率重烙
            RootGrid.LayoutTransform = Math.Abs(k - 1) < 0.001 ? null : new ScaleTransform(k, k);

            // WPF 对重挂子窗口的 WM_SIZE 处理不可靠（布局尺寸会卡旧值）→ 显式按它自己的
            // 账本设 Width/Height。它随后发起的 SetWindowPos 会被 ForceCoverHook 钳回物理
            // 真值，内容比例由上面的 LayoutTransform 修正，三者对任何 DPI 错位组合都收敛。
            Width = Monitor.Physical.Width * ct.TransformFromDevice.M11;
            Height = Monitor.Physical.Height * ct.TransformFromDevice.M22;
            Log.Write($"[{MonKey}] covered; wpf scale={believed:F2} actual={actual:F2} correction={k:F3} wpf-size={Width:F0}x{Height:F0}");
        }
        ApplyWallpaperMode(); // 动态壁纸模式进入/矩形同步（WE 不在 = no-op 保持镜像）
    }

    // WPF 的尺寸账本（DIU）和窗口 DPI 在混合缩放下会打架，它总想按自己的理解改窗口大小。
    // 治本：钩住 WM_WINDOWPOSCHANGING，凡挂载后一律强制回本显示器矩形（父客户区坐标）。
    private const int WM_WINDOWPOSCHANGING = 0x0046;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private IntPtr ForceCoverHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 吞掉 WM_CONTEXTMENU：不拦的话 DefWindowProc 会把它转发给父窗口 DefView，
        // 原生桌面菜单就跟我们的菜单一前一后都弹出来（我们的菜单走 MouseRightButtonUp，不受影响）
        if (msg == Native.WM_CONTEXTMENU)
        {
            handled = true;
            return IntPtr.Zero;
        }
        // 主进程同线程菜单（序列化路径）：owner-draw 项回放捕获位图
        if (msg is 0x002C /* WM_MEASUREITEM */ && MenuSnapshot.OnMeasureItem(NativeMenuPresenter.Current, lParam))
        {
            handled = true;
            return (IntPtr)1;
        }
        if (msg is 0x002B /* WM_DRAWITEM */ && MenuSnapshot.OnDrawItem(NativeMenuPresenter.Current, lParam))
        {
            handled = true;
            return (IntPtr)1;
        }
        // 菜单开着期间吞 WM_CANCELMODE：激活风暴的迟到落地会经它杀菜单（DefWindowProc
        // 收到即 EndMenu）。真点击外部的关闭走菜单自身的捕获判定，不经这条路，吞掉无副作用。
        if (msg == (int)Native.WM_CANCELMODE && NativeMenuPresenter.MenuOpen)
        {
            handled = true;
            return IntPtr.Zero;
        }
        // 子菜单白块修复：新弹出的子菜单窗口补绘一次（见 NativeMenuPresenter）
        if (msg is 0x0117 /* WM_INITMENUPOPUP */) NativeMenuPresenter.OnInitMenuPopup(hwnd, wParam);
        if (msg is 0x0113 /* WM_TIMER */ && NativeMenuPresenter.OnTimer(hwnd, wParam))
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_WINDOWPOSCHANGING && _attached && _forceRectValid)
        {
            var r = _forceRect;
            var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if (wp.cx != r.Width || wp.cy != r.Height || wp.x != r.Left || wp.y != r.Top)
            {
                wp.x = r.Left; wp.y = r.Top; wp.cx = r.Width; wp.cy = r.Height;
                wp.flags &= ~(uint)0x0003; // 清 SWP_NOSIZE | SWP_NOMOVE
                System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    // ── 图标集合 ──────────────────────────────────────────────

    /// <summary>协调器分发本窗口的图标子集（归属本显示器 + 主屏兜底的孤儿）。</summary>
    internal void RefreshItems(IReadOnlyList<DesktopEntry> entries, IReadOnlyList<string>? missingNames = null)
    {
        var alive = new HashSet<string>(entries.Select(en => en.Path));

        foreach (var gone in _icons.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            var iv = _icons[gone];
            IconCanvas.Children.Remove(iv.Root);
            _selection.Remove(iv);
            if (_focusIcon == iv) _focusIcon = null;
            _icons.Remove(gone);
        }

        bool added = false;
        foreach (var en in entries)
        {
            if (_icons.ContainsKey(en.Path)) continue;
            var iv = CreateIconVisual(en);
            iv.Canon = Desktop.EffectiveCanon(Path.GetFileName(en.Path));
            _icons[en.Path] = iv;
            IconCanvas.Children.Add(iv.Root);
            added = true;
        }

        // 问号占位（布局条目在、文件不在，跨机导入场景）：随分发增删
        var missSet = new HashSet<string>(missingNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _missing.Keys.Where(k => !missSet.Contains(k)).ToList())
        {
            IconCanvas.Children.Remove(_missing[gone]);
            _missing.Remove(gone);
        }
        foreach (var n in missSet)
        {
            if (_missing.ContainsKey(n)) continue;
            var root = CreateMissingVisual(n);
            _missing[n] = root;
            IconCanvas.Children.Add(root);
            added = true;
        }

        if (added || _icons.Count > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LayoutAll(animated: true));
    }

    // ── 跨机导入的 missing 项 = macOS 丢失别名同款问号占位（机主 spec）───────
    // 布局里有条目、本机没有对应文件：不擅自删布局项，渲染成大问号图标占位；
    // 用户右键"从布局中移除"自行清理。占位是惰性视觉：不可拖、不可开，只可移除。

    private readonly Dictionary<string, Border> _missing = new(StringComparer.OrdinalIgnoreCase);
    private const uint ID_MISSING_REMOVE = 0x7301;

    private Border CreateMissingVisual(string name)
    {
        var q = new TextBlock
        {
            Text = "?",
            Foreground = Brushes.White,
            FontSize = 40 * S,
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -4 * S, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.6 },
        };
        var plate = new Border
        {
            Width = 58 * S, Height = 58 * S,
            CornerRadius = new CornerRadius(12 * S),
            Background = new SolidColorBrush(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = q,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconPlate = new Border
        {
            Height = (64 + 12) * S, // 与真实图标 iconPlate（64 图 + 6×2 padding）同高，标签基线对齐
            Child = plate,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        string labelText = TruncateLabel(name);
        var label = new TextBlock
        {
            Text = labelText,
            Foreground = Brushes.White,
            FontSize = 12 * S,
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34 * S,
            Opacity = 0.9,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.85 },
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        var labelPlate = new Border
        {
            CornerRadius = new CornerRadius(6 * S),
            Padding = new Thickness(5 * S, 1, 5 * S, 2),
            Background = Brushes.Transparent,
            Child = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = CellW - 4 * S,
        };

        var stack = new StackPanel();
        stack.Children.Add(iconPlate);
        stack.Children.Add(labelPlate);
        var root = new Border
        {
            Width = CellW,
            Child = stack,
            Background = Brushes.Transparent,
            ToolTip = new ToolTip { Content = $"{name}\n{L.T("此项目在本机不存在（来自导入的布局）", "This item does not exist on this machine (from an imported layout)")}" },
        };
        string n = name;
        root.MouseLeftButtonDown += (_, e) => e.Handled = true; // 别漏给画布启动框选
        root.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var pt = PointToScreen(e.GetPosition(this));
            PrepareForMenu();
            uint cmd = NativeMenuPresenter.Track(_hwnd,
                new List<MenuSnapshot.Item> { new() { Id = ID_MISSING_REMOVE, Text = L.T("从布局中移除", "Remove from Layout") } },
                (int)pt.X, (int)pt.Y);
            if (cmd == ID_MISSING_REMOVE)
            {
                LayoutFile.Remove(n);
                LayoutFile.Save();
                Desktop.RefreshAll();
            }
        };
        ApplyCacheMode(root);
        return root;
    }

    /// <summary>问号占位的摆放：恒按规范锚距现场推导（自由/网格/叠放模式一律如此——
    /// 占位不是文件，不参与列流与聚堆；叠放下可能与堆重叠，属可接受的临时态）。</summary>
    private void PlaceMissing(bool animated)
    {
        foreach (var (name, root) in _missing)
        {
            var c = Desktop.EffectiveCanon(name);
            if (c == null) continue; // 条目刚被移除，等下一轮分发清理视觉
            var (l, t) = CanonToPos(c);
            MoveElement(root, l, t, animated, EaseGlide, GlideMs);
        }
    }

    private IconVisual CreateIconVisual(DesktopEntry en, bool deferIcon = false)
    {
        // deferIcon（文件夹堆叠子项专用）：不在 UI 线程同步取图。上百张多 MB 图片的
        // 冷缩略图提取一张上百 ms，同步会把桌面冻几十秒（120 项真机实测 ~40s）——
        // 子项先空盘上场，LoadFolderStackIconsAsync 后台逐个拉、渐进回填
        var img = new Image
        {
            Width = 64 * S, Height = 64 * S,
            Source = deferIcon ? null : IconLoader.Load(en.Path, IconPx),
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        // 文件夹包一层 Grid 当堆叠角标挂点。文件保持裸 Image：饼堆 scrub 读
        // IconPlate.Child 为 Image（堆成员只会是文件——分类用建档快照 IsDir，与包装决策恒一致）
        bool isDir = !en.Path.StartsWith("::") && Directory.Exists(en.Path);
        FrameworkElement iconContent = img;
        if (isDir)
        {
            var wrap = new Grid();
            wrap.Children.Add(img);
            iconContent = wrap;
        }

        var iconPlate = new Border
        {
            CornerRadius = new CornerRadius(8 * S),
            Padding = new Thickness(6 * S),
            Background = Brushes.Transparent,
            Child = iconContent,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        string labelText = TruncateLabel(en.DisplayName);
        var label = new TextBlock
        {
            Text = labelText,
            Foreground = Brushes.White,
            FontSize = 12 * S,
            // mac 质感：中英文都上 Bold（机主反馈 SemiBold 英文仍偏细；Windows 自带，免费合法）
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, // 测量偏差时的兜底
            MaxHeight = 34 * S,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.85 },
        };
        // 小字号必须走 Display 模式（对齐像素网格），配合 MoveIcon 的整数坐标吸附——
        // 亚像素落位是"有的标签清晰有的糊"的元凶
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        var labelPlate = new Border
        {
            CornerRadius = new CornerRadius(6 * S),
            Padding = new Thickness(5 * S, 1, 5 * S, 2),
            Background = Brushes.Transparent,
            Child = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = CellW - 4 * S,
        };

        var stack = new StackPanel();
        stack.Children.Add(iconPlate);
        stack.Children.Add(labelPlate);

        var root = new Border
        {
            Width = CellW,
            Child = stack,
            Background = Brushes.Transparent, // 命中测试
        };

        // 被截断的名字悬停显示全名（短名不设，免打扰）
        if (labelText != en.DisplayName)
            root.ToolTip = new ToolTip { Content = en.DisplayName };

        var iv = new IconVisual { Entry = en, Root = root, IconPlate = iconPlate, LabelPlate = labelPlate, Label = label, IsDir = isDir };
        ApplyCacheMode(root); // 镜像=Root 挂 BitmapCache（治渐隐 alpha 打洞背板）；动态=按设置摘 Effect
        root.MouseLeftButtonDown += (s, e) => OnIconMouseDown(iv, e);
        root.MouseMove += (s, e) => OnIconMouseMove(iv, e);
        root.MouseLeftButtonUp += (s, e) => OnIconMouseUp(iv, e);
        root.MouseRightButtonUp += (s, e) => OnIconRightClick(iv, e);
        // 捕获被外部打断（菜单进程抢前台等）→ 按下状态清零，否则之后一次悬停划过就会误触发拖拽
        root.LostMouseCapture += (s, e) => { if (!iv.Dragging) iv.MouseDown = false; };
        return iv;
    }

    // ── 标签截断（Finder 式：两行装不下时中间省略、保住扩展名） ──

    /// <summary>标签两行内是否放得下（与 TextBlock 同字体/同 Display 模式/同宽度测量）。</summary>
    private bool LabelFits(string text)
    {
        var ft = new FormattedText(text,
            System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(LabelFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            12 * S, Brushes.White, null, TextFormattingMode.Display,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = CellW - 14 * S, // labelPlate MaxWidth(CellW-4·S) − 左右 Padding(5·S+5·S)
            Trimming = TextTrimming.None,
        };
        return ft.Height <= 34.5 * S; // TextBlock MaxHeight=34·S（两行）
    }

    /// <summary>Finder 行为：溢出两行时中间省略，尾部保"扩展名+3 字符"（尾部区分度高，
    /// mac 上实测规格见 research/notes/macos-finder-手感调研.md）。放得下则原样返回。</summary>
    private string TruncateLabel(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || LabelFits(name)) return name;

            string ext = "";
            try { ext = Path.GetExtension(name); } catch { }
            if (ext.Length is 0 or > 8) ext = ""; // 无扩展名/超长伪扩展名按纯文本截
            string stem = ext.Length > 0 ? name[..^ext.Length] : name;
            int tailChars = Math.Min(3, Math.Max(0, stem.Length - 1));
            string tail = stem[^tailChars..] + ext;

            // 二分最长前缀："prefix…tail" 恰好塞进两行
            int lo = 1, hi = stem.Length - tailChars;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (LabelFits($"{stem[..mid]}…{tail}")) lo = mid;
                else hi = mid - 1;
            }
            return $"{stem[..lo]}…{tail}";
        }
        catch { return name; } // 测量失败原样返回，交给 CharacterEllipsis 兜底
    }

    // ── 布局引擎 ──────────────────────────────────────────────

    private (double W, double H) WorkSize => (RootGrid.ActualWidth, RootGrid.ActualHeight);

    private double PitchX => CellW + GapX;
    private double PitchY => CellH + GapY;

    // ── 首行下沉（给第三方顶部菜单栏让位，issue #11 的"贴顶"观感同源） ──
    // 只作用于**显示层**：CellPos/RowsPerColumn/SnapToGrid/PosToCell/MarkFootprint 一律从
    // GridTop 起算；CellToCanon/CanonToCell/CanonToPos 坚决不掺——Canon 坐标系必须与关闭态
    // 完全一致（"绝不回写"红线）。若把 MarginTop 本身加 56，存量 Canon 反推行号会正好落在
    // Math.Round 的 .5 中点上（56 恰为默认档半行），ToEven 舍入导致隔行成对碰撞、图标洗牌。
    // 56 = 默认档半行 (104+8)/2，DIU 常数不乘 S：菜单栏高度不随图标档缩放。
    private const double SinkAmount = 56;

    /// <summary>生效的下沉量。退化保护：下沉后连一整行都排不下（矮屏/投影）就放弃下沉——
    /// 否则 RowsPerColumn 的 Math.Max(1,…) 兜底行会被顶进 MarginBottom 保留区甚至屏外。</summary>
    private double SinkY
    {
        get
        {
            if (!Config.FirstRowSink) return 0;
            var (_, h) = WorkSize;
            return h - MarginTop - SinkAmount - MarginBottom >= CellH ? SinkAmount : 0;
        }
    }

    /// <summary>显示网格的首行顶边。显示路径用它；Canon 路径恒用 MarginTop。</summary>
    private double GridTop => MarginTop + SinkY;

    /// <summary>规范锚距 → 当前尺寸下的显示左上角（固定间距、右上/近边锚定，只做屏内钳制）。</summary>
    private (double L, double T) CanonToPos(CanonPos c)
    {
        var (w, h) = WorkSize;
        double cx = w - c.RightDist;
        double cy = c.FromBottom ? h - c.EdgeDist : c.EdgeDist;
        double l = Math.Clamp(cx - CellW / 2, 0, Math.Max(0, w - CellW));
        double t = Math.Clamp(cy - CellH / 2, 0, Math.Max(0, h - CellH));
        return (l, t);
    }

    /// <summary>显示左上角 → 规范锚距（自由摆放用：近边锚定 Y——上 60% 锚顶、下 40% 锚底）。</summary>
    private CanonPos PosToCanon(double l, double t)
    {
        var (w, h) = WorkSize;
        double cx = l + CellW / 2, cy = t + CellH / 2;
        bool fromBottom = cy > h * 0.6;
        return new CanonPos(w - cx, fromBottom ? h - cy : cy, fromBottom);
    }

    /// <summary>网格格 → 规范锚距（纯 col/row 的函数，与分辨率无关；网格恒上锚）。
    /// 注：Canon 里烙进的是**当前档**的 pitch，但只对 Canon==null 的新图标/显式落点调用，
    /// 已有图标切档时不经此路（LayoutAll 非空分支只推导显示位置），故红线不破。</summary>
    private CanonPos CellToCanon(int col, int row) =>
        new(MarginRight + CellW / 2 + col * PitchX, MarginTop + CellH / 2 + row * PitchY, FromBottom: false);

    /// <summary>规范锚距 → 网格格（当前尺寸）。近边锚底的先换算回中心 Y。</summary>
    private (int col, int row) CanonToCell(CanonPos c)
    {
        double topOfCenter = c.FromBottom ? WorkSize.H - c.EdgeDist : c.EdgeDist;
        int col = (int)Math.Round((c.RightDist - MarginRight - CellW / 2) / PitchX);
        int row = (int)Math.Round((topOfCenter - MarginTop - CellH / 2) / PitchY);
        return (col, row);
    }

    /// <summary>mac 式默认格：右上锚定、列内向下、列向左扩展。col/row → 左上角坐标</summary>
    private (double L, double T) CellPos(int col, int row)
    {
        var (w, _) = WorkSize;
        return (w - MarginRight - CellW - col * (CellW + GapX), GridTop + row * (CellH + GapY));
    }

    private int RowsPerColumn()
    {
        var (_, h) = WorkSize;
        return Math.Max(1, (int)((h - GridTop - MarginBottom + GapY) / (CellH + GapY)));
    }

    /// <summary>最左可用列：保证最左格左上角 ≥ MarginLeft，杜绝左缘半截图标。</summary>
    private int MaxCol()
    {
        var (w, _) = WorkSize;
        return Math.Max(0, (int)((w - MarginRight - CellW - MarginLeft) / (CellW + GapX)));
    }

    /// <summary>吸附到最近的右锚网格格子。</summary>
    private (double L, double T) SnapToGrid(double l, double t)
    {
        int col = (int)Math.Round((WorkSize.W - MarginRight - CellW - l) / (CellW + GapX));
        int row = (int)Math.Round((t - GridTop) / (CellH + GapY));
        col = Math.Clamp(col, 0, MaxCol());
        row = Math.Clamp(row, 0, RowsPerColumn() - 1);
        return CellPos(col, row);
    }

    private (int, int) ClampCell((int, int) cell) =>
        (Math.Clamp(cell.Item1, 0, MaxCol()), Math.Clamp(cell.Item2, 0, RowsPerColumn() - 1));

    /// <summary>自由摆放：图标不吸格、可跨格，把显示脚印（CellW×CellH）盖到的所有格子
    /// 标记为已占。新图标种子/新建文件夹找空格时避让用——不标记的话右上列流会把新图标
    /// 直接叠在已有图标上（真机踩坑：新建文件落在 New Folder 同一格）。</summary>
    private void MarkFootprint(HashSet<(int, int)> occupied, double l, double t)
    {
        var (w, _) = WorkSize;
        double strideX = CellW + GapX, strideY = CellH + GapY;
        double colExact = (w - MarginRight - CellW - l) / strideX;
        double rowExact = (t - GridTop) / strideY;
        for (int col = (int)Math.Floor(colExact); col <= (int)Math.Ceiling(colExact); col++)
        {
            if (col < 0 || col > MaxCol()) continue;
            double cl = w - MarginRight - CellW - col * strideX;
            if (cl + CellW <= l || cl >= l + CellW) continue; // 该列与脚印无横向重叠
            for (int row = (int)Math.Floor(rowExact); row <= (int)Math.Ceiling(rowExact); row++)
            {
                if (row < 0 || row >= RowsPerColumn()) continue;
                double ct = GridTop + row * strideY;
                if (ct + CellH <= t || ct >= t + CellH) continue;
                occupied.Add((col, row));
            }
        }
    }

    /// <summary>找空格用的占用集合：自由摆放按显示脚印（可跨格），网格按目标格。</summary>
    private HashSet<(int, int)> OccupiedCellsForSeeding()
    {
        var occ = new HashSet<(int, int)>();
        foreach (var name in _missing.Keys)
            if (Desktop.EffectiveCanon(name) is { } mc)
            {
                var (ml, mt) = CanonToPos(mc);
                MarkFootprint(occ, ml, mt);
            }
        foreach (var iv in _icons.Values.Where(i => i.Canon != null))
        {
            if (Config.FreePlacement)
            {
                var (l, t) = CanonToPos(iv.Canon!);
                MarkFootprint(occ, l, t);
            }
            else
            {
                occ.Add(ClampCell(CanonToCell(iv.Canon!)));
            }
        }
        return occ;
    }

    /// <summary>
    /// 全量重排（单一规范布局，macOS 模型）：已有 Canon 的图标只**现场推导**显示位置，
    /// **绝不回写** Canon——所以切分辨率不改变布局，切回精确还原。放不下才折行/钳制（仅显示）。
    /// 只有新图标（Canon==null）才分配 Canon。
    /// </summary>
    internal void LayoutAll(bool animated)
    {
        var (w, h) = WorkSize;
        if (w < 1 || h < 1) return;
        Log.Write($"[{MonKey}] layout pass worksize={w:F0}x{h:F0}{(Config.FreePlacement ? " [free]" : "")}{(Config.UseStacks ? " [stacks]" : "")}");

        PlaceMissing(animated); // 问号占位恒按锚距摆放，与模式无关

        // 使用叠放（macOS Use Stacks）：独立的自动整理模式，不碰规范布局，关闭即恢复
        if (Config.UseStacks) { LayoutStacks(animated); return; }
        if (_stackPiles.Count > 0 || _folderStack != null || _hasStackBadges) ClearStacks();

        // 规范布局是唯一事实来源：重排前全员取有效 Canon（含归属离场显示器的孤儿——推导显示、不回写）
        foreach (var iv in _icons.Values)
        {
            iv.Canon = Desktop.EffectiveCanon(Path.GetFileName(iv.Entry.Path));
            // 自愈：拖拽/剪切之外不允许有半透明残留（曾有捕获被打断导致留影卡死的实例）
            if (iv.Root.Opacity < 1 && !_dragGhosts.Contains(iv) && !_cutIcons.Contains(iv))
                iv.Root.Opacity = 1;
        }

        var placed = new HashSet<(int, int)>();

        // 问号占位的脚印也计入占用，新图标列流别叠上去
        foreach (var name in _missing.Keys)
            if (Desktop.EffectiveCanon(name) is { } mc)
            {
                var (ml, mt) = CanonToPos(mc);
                MarkFootprint(placed, ml, mt);
            }

        if (Config.FreePlacement)
        {
            // 自由摆放：按锚距现场推导（只屏内钳制，不吸格/不避让、不回写 Canon）。
            // 但要把脚印占格记入 placed——否则下面的新图标列流对着空集合避让，
            // 新建文件必然叠在右上角已有图标上（真机踩坑）
            foreach (var iv in _icons.Values.Where(i => i.Canon != null))
            {
                var (l, t) = CanonToPos(iv.Canon!);
                MoveIcon(iv, l, t, animated);
                MarkFootprint(placed, l, t);
            }
        }
        else
        {
            // 网格模式：把 Canon 推导成目标格，放不下则折行/避让（仅显示，不回写 Canon）
            var withCanon = _icons.Values.Where(i => i.Canon != null)
                .Select(i => (Iv: i, Cell: ClampCell(CanonToCell(i.Canon!))))
                .OrderBy(x => x.Cell.Item1).ThenBy(x => x.Cell.Item2).ToList();
            foreach (var (iv, want) in withCanon)
            {
                var cell = NearestFreeCell(want, placed);
                placed.Add(cell);
                var (l, t) = CellPos(cell.Item1, cell.Item2);
                MoveIcon(iv, l, t, animated); // 注意：不写 iv.Canon
            }
        }

        // 没有位置的新图标：按 mac 式右上列流填进空格，并**分配** Canon（唯一写 Canon 的地方）
        int rows = RowsPerColumn();
        int col = 0, row = 0;
        foreach (var iv in SortForArrange(_icons.Values.Where(i => i.Canon == null)))
        {
            while (placed.Contains((col, row))) Advance(ref col, ref row, rows);
            placed.Add((col, row));
            var (l, t) = CellPos(col, row);
            iv.Canon = CellToCanon(col, row);
            LayoutFile.Set(MonKey, Path.GetFileName(iv.Entry.Path), iv.Canon);
            MoveIcon(iv, l, t, animated);
        }
        LayoutFile.Save();
    }

    // ── 使用叠放（macOS Use Stacks v1：文件按类型聚堆，文件夹/虚拟项独立） ──
    // 语义与 macOS 一致：叠放是自动整理模式——开启期间列流自动排布、不写规范布局，
    // 关闭后 LayoutAll 按 Canon 恢复原摆放。点击堆展开/再点收起，展开项是真实图标
    // （可开/可拖出）。分组依据 v1 只有"类型"。

    private sealed class PileVisual
    {
        public Border Root = null!;
        // 收起态 = 真实内容扇形（后/中/前三层真图标斜向叠放，含"其他"——macOS 收起的堆一律真图标）
        public Grid FanGroup = null!;
        public Image IconFront = null!, IconMid = null!, IconBack = null!;
        // 展开态 = 语义占位（叠层卡片 + 居中收起箭头）：macOS 展开后原堆位变成"点此收起"按钮
        public Grid CardGroup = null!;
        public TextBlock Label = null!;
        public Border LabelPlate = null!;
        public string? FrontPath, MidPath, BackPath; // 已加载的真实图标路径缓存，跳过重复取图
        // 刮擦预览（macOS scrub = 悬停 + 滚轮/双指滚动轮换，非鼠标移动）状态
        public List<IconVisual> Members = new();
        public bool Expanded;
        public int ScrubIndex = -1; // -1 = 未在刮擦，静息态
        public ImageSource? RestingFrontIcon;
    }

    private readonly Dictionary<string, PileVisual> _stackPiles = new();
    private string? _expandedStack;

    private static readonly string OtherKind = L.T("其他", "Other"); // 混杂兜底分组名（仅"类型"分组会产生）

    private static readonly (string Kind, string[] Exts)[] StackKindTable =
    {
        (L.T("应用程序", "Applications"), new[] { ".lnk", ".url", ".exe", ".appref-ms" }),
        (L.T("图片", "Images"), new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".ico", ".svg" }),
        (L.T("文档", "Documents"), new[] { ".txt", ".md", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".csv" }),
        (L.T("压缩包", "Archives"), new[] { ".zip", ".7z", ".rar", ".gz", ".tar", ".iso" }),
    };

    private static string StackKindOf(DesktopEntry en)
    {
        var ext = Path.GetExtension(en.Path).ToLowerInvariant();
        foreach (var (kind, exts) in StackKindTable)
            if (exts.Contains(ext)) return kind;
        return OtherKind;
    }

    // ── 分组依据 v2：修改日期 / 大小（机主认可方向，菜单 ID 早留了 0x700E 起） ──
    // 与"类型"不同，这两种分组下每一档都是真实文件的正常聚合，没有"其他"式的混杂兜底：
    // UpdatePileVisual 只在 StackGroupBy=="kind" 且档名==OtherKind 时才退化成语义占位。

    private static readonly string[] DateBuckets = { L.T("今天", "Today"), L.T("昨天", "Yesterday"), L.T("本周", "This Week"), L.T("本月", "This Month"), L.T("更早", "Earlier") };
    private static readonly string[] SizeBuckets = { L.T("小型", "Small"), L.T("中型", "Medium"), L.T("大型", "Large"), L.T("超大型", "Huge") };

    private static string StackDateBucketOf(DesktopEntry en)
    {
        DateTime t;
        try { t = File.GetLastWriteTime(en.Path); } catch { return DateBuckets[^1]; }
        int days = (DateTime.Now.Date - t.Date).Days;
        return days switch
        {
            <= 0 => DateBuckets[0],
            1 => DateBuckets[1],
            <= 7 => DateBuckets[2],
            <= 30 => DateBuckets[3],
            _ => DateBuckets[4],
        };
    }

    private static string StackSizeBucketOf(DesktopEntry en)
    {
        long sz;
        try { sz = new FileInfo(en.Path).Length; } catch { return SizeBuckets[0]; }
        const long MB = 1024L * 1024, GB = 1024 * MB;
        if (sz < MB) return SizeBuckets[0];
        if (sz < 100 * MB) return SizeBuckets[1];
        if (sz < GB) return SizeBuckets[2];
        return SizeBuckets[3];
    }

    /// <summary>当前分组依据下的分类函数 + 档位显示顺序，供 LayoutStacks 统一驱动。</summary>
    private static (Func<DesktopEntry, string> Classify, IEnumerable<string> Order) StackGrouping() =>
        Config.StackGroupBy switch
        {
            "date" => (StackDateBucketOf, DateBuckets),
            "size" => (StackSizeBucketOf, SizeBuckets),
            _ => (StackKindOf, StackKindTable.Select(k => k.Kind).Append(OtherKind)),
        };

    private void ClearStacks()
    {
        foreach (var p in _stackPiles.Values) IconCanvas.Children.Remove(p.Root);
        _stackPiles.Clear();
        _expandedStack = null;
        _folderStackPending = null;
        if (_folderStack != null) CollapseFolderStack(_folderStack, animated: false);
        if (_hasStackBadges)
        {
            foreach (var iv in _icons.Values)
                if (iv.StackBadge != null) UpdateStackBadge(iv, show: false, expanded: false);
            _hasStackBadges = false;
        }
        foreach (var iv in _icons.Values) iv.Root.Visibility = Visibility.Visible;
    }

    /// <summary>改图标尺寸档时清空全部视觉（图标/问号/堆），让 RefreshAll 按新档从工厂重建
    /// （RefreshItems 只为新路径建图标，不清空就不会重建成新尺寸）。Canon 不动，重排现场推导。</summary>
    internal void TearDownVisuals()
    {
        ClearStacks();
        foreach (var iv in _icons.Values) IconCanvas.Children.Remove(iv.Root);
        _icons.Clear();
        foreach (var root in _missing.Values) IconCanvas.Children.Remove(root);
        _missing.Clear();
        _selection.Clear();
        _focusIcon = null;
        _previewOpen = false;
    }

    private void LayoutStacks(bool animated)
    {
        int rows = RowsPerColumn();
        int col = 0, row = 0;
        (double L, double T) Take()
        {
            var pos = CellPos(col, row);
            Advance(ref col, ref row, rows);
            return pos;
        }

        // 用建档快照 IsDir 而非现场 Directory.Exists：外部删除/断链的文件夹在 RefreshItems
        // 清走前保持单摆（不跳饼堆），与图标的 Grid 包装决策恒一致（scrub 强转安全的根基）
        static bool IsSingle(IconVisual i) => i.Entry.Path.StartsWith("::") || i.IsDir;
        var virt = _icons.Values.Where(i => i.Entry.Path.StartsWith("::"));
        var dirs = _icons.Values.Where(i => !i.Entry.Path.StartsWith("::") && IsSingle(i))
            .OrderBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        // 文件夹堆叠视图的世界一致性：文件夹没了（删除/改名/移交别屏）、标记被摘、
        // 或展开键已换人（点开别的堆）→ 收起销毁，绝不留幽灵子项
        if (_folderStack is { } fsCheck)
        {
            bool valid = _expandedStack == DirKey(fsCheck.FolderPath)
                && IsStackFolder(fsCheck.FolderPath)
                && _icons.Values.Any(i => PathEq(i.Entry.Path, fsCheck.FolderPath));
            if (!valid)
            {
                if (_expandedStack == DirKey(fsCheck.FolderPath)) _expandedStack = null; // 文件夹/标记没了：展开态作废
                CollapseFolderStack(fsCheck, animated);
            }
        }

        foreach (var iv in virt.Concat(dirs))
        {
            iv.Root.Visibility = Visibility.Visible;
            var (l, t) = Take();
            MoveIcon(iv, l, t, animated);
            bool flagged = !iv.Entry.Path.StartsWith("::") && IsStackFolder(iv.Entry.Path);
            bool dirExpanded = flagged && _folderStack is { } fs && PathEq(fs.FolderPath, iv.Entry.Path);
            UpdateStackBadge(iv, flagged, dirExpanded);
            // 展开的文件夹堆叠：子项紧跟文件夹本体列流铺开（后续文件夹/堆自动被挤走，与饼堆展开同轨）
            if (dirExpanded) LayoutFolderStackChildren(_folderStack!, Take, animated);
        }

        var (classify, order) = StackGrouping();
        var groups = _icons.Values.Where(i => !IsSingle(i))
            .GroupBy(i => classify(i.Entry))
            .ToDictionary(g => g.Key,
                g => g.OrderBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList());
        var kindOrder = order.Where(groups.ContainsKey).ToList();
        // 展开中的堆消失（清空/改型）→ 清展开态；"dir:" 键是文件夹堆叠的，由上方一致性检查负责
        if (_expandedStack != null && !_expandedStack.StartsWith(DirKeyPrefix, StringComparison.Ordinal)
            && !kindOrder.Contains(_expandedStack)) _expandedStack = null;

        // 收起动画的"飞回堆位"批次：动画播完才 Collapsed。定时器触发时按"那一刻"的分组/展开
        // 状态重新核实——期间被再点开/换分组就跳过隐藏，不需要显式取消定时器，天然免疫重入。
        var justCollapsing = new List<IconVisual>();

        foreach (var kind in kindOrder)
        {
            var members = groups[kind];
            var pile = GetOrCreatePile(kind);
            bool expanded = _expandedStack == kind;
            UpdatePileVisual(pile, kind, members, expanded);
            pile.Root.ToolTip = $"{kind} · {members.Count}{L.T(" 项", members.Count == 1 ? " item" : " items")}";
            pile.LabelPlate.Background = expanded ? Accent.LabelBrush : Brushes.Transparent;

            var (l, t) = Take();
            double pl = Math.Round(l), pt = Math.Round(t); // 堆槽位坐标 = 收起成员的停靠点
            // 堆自己被别的堆展开挤走时也要滑过去——机主反馈：直接瞬移很生硬
            MoveElement(pile.Root, pl, pt, animated, EaseGlide, GlideMs);

            foreach (var m in members)
            {
                bool wasVisible = m.Root.Visibility == Visibility.Visible;
                if (expanded)
                {
                    // 展开：坐标此刻已停在堆位（见下），从堆位弹出飞向目标格 + 渐显
                    m.Root.Visibility = Visibility.Visible;
                    var (ml, mt) = Take();
                    MoveElement(m.Root, ml, mt, animated, EaseSpringOut, ExpandMs);
                    if (animated && !wasVisible)
                    {
                        m.Root.Opacity = 0.2;
                        FadeTo(m.Root, 1, 200);
                    }
                    else
                    {
                        ResetOpacity(m.Root); // 可能正处于收起渐隐中被再次点开
                        if (_cutIcons.Contains(m)) m.Root.Opacity = 0.5;
                    }
                }
                else if (wasVisible)
                {
                    // 刚收起（含首次开启叠放）：加速吸回堆位，行程后段渐隐，播完才 Collapsed
                    MoveElement(m.Root, pl, pt, animated, EaseInhale, CollapseMs);
                    if (animated)
                    {
                        FadeTo(m.Root, 0, CollapseMs - 120, 120); // 后 60% 行程里淡出，到位即无形
                        justCollapsing.Add(m);
                    }
                    else m.Root.Visibility = Visibility.Collapsed; // 非动画通道（冷启动）：直接落位
                }
                else
                {
                    // 保持收起：坐标静默跟随堆位（堆增减会移动槽位），不触发可见动画
                    MoveElement(m.Root, pl, pt, false, EaseGlide, GlideMs);
                }
            }
        }

        foreach (var k in _stackPiles.Keys.Where(k => !kindOrder.Contains(k)).ToList())
        {
            IconCanvas.Children.Remove(_stackPiles[k].Root);
            _stackPiles.Remove(k);
        }

        if (justCollapsing.Count > 0)
        {
            var batch = justCollapsing;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // 归属用触发时刻的分组函数现算：窗口内换过分组依据的话，捕获的旧堆名会误判
                var (classify, _) = StackGrouping();
                foreach (var m in batch)
                {
                    if (Config.UseStacks && _expandedStack != classify(m.Entry))
                    {
                        m.Root.Visibility = Visibility.Collapsed;
                        ResetOpacity(m.Root); // 隐藏后归位全显，下次展开从干净状态渐显
                        if (_cutIcons.Contains(m)) m.Root.Opacity = 0.5; // 剪切半透明态别被冲掉
                    }
                }
            };
            timer.Start();
        }
    }

    /// <summary>真实内容扇形的一层：小尺寸/低透明度模拟"压在下面"，DropShadow 帮衬深浅分层
    /// （很多文件图标本身透明底，紧贴壁纸时全靠阴影才分得出层次）。</summary>
    private static Image MakePileLayer(double size, double opacity, HorizontalAlignment ha, VerticalAlignment va, Thickness margin, double shadowBlur, double shadowOpacity)
    {
        var im = new Image
        {
            Width = size, Height = size, Opacity = opacity,
            HorizontalAlignment = ha, VerticalAlignment = va, Margin = margin,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect { BlurRadius = shadowBlur, ShadowDepth = 1.5, Opacity = shadowOpacity, Color = Colors.Black },
        };
        RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
        return im;
    }

    private PileVisual GetOrCreatePile(string kind)
    {
        if (_stackPiles.TryGetValue(kind, out var existing)) return existing;

        // 真实内容扇形：后→中→前三层真图标斜向叠放（前锚左下、后锚右上，mac 堆叠手感），
        // 按成员数决定 mid/back 是否显示（见 UpdatePileVisual）。
        var iconBack = MakePileLayer(46 * S, 0.72, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0), 3 * S, 0.3);
        var iconMid = MakePileLayer(53 * S, 0.88, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 6 * S, 5 * S, 0), 3 * S, 0.35);
        var iconFront = MakePileLayer(60 * S, 1.0, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(0), 4 * S, 0.4);

        // 收起态扇形组（三层真图标一体隐显/渐变）
        var fanGroup = new Grid();
        fanGroup.Children.Add(iconBack);
        fanGroup.Children.Add(iconMid);
        fanGroup.Children.Add(iconFront);

        // 展开态语义占位（机主定案的简化样式）：一个白色半透明圆角矩形 + 居中向下 V 箭头
        // = "点此收起"按钮。箭头带轻阴影，半透明底上不糊。
        var chevron = new System.Windows.Shapes.Path
        {
            // 用 LayoutTransform 缩放几何+描边，避开路径迷你语言字符串的小数分隔符本地化坑
            Data = Geometry.Parse("M 0,0 L 11,11 L 22,0"),
            Stroke = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5 * S, 0, 0), // V 形视觉重心偏上，微降回正
            LayoutTransform = S == 1.0 ? Transform.Identity : new ScaleTransform(S, S),
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.45 },
        };
        var cardGroup = new Grid { Opacity = 0, IsHitTestVisible = false }; // 初始收起态：只见扇形
        cardGroup.Children.Add(new Border
        {
            Width = 58 * S, Height = 58 * S,
            CornerRadius = new CornerRadius(12 * S),
            Background = new SolidColorBrush(Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = chevron,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var plate = new Grid { Width = 76 * S, Height = 74 * S, HorizontalAlignment = HorizontalAlignment.Center };
        plate.Children.Add(fanGroup);
        plate.Children.Add(cardGroup);

        var label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12 * S,
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.85 },
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        var labelPlate = new Border
        {
            CornerRadius = new CornerRadius(6 * S),
            Padding = new Thickness(5 * S, 1, 5 * S, 2),
            Background = Brushes.Transparent,
            Child = label,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel();
        stack.Children.Add(plate);
        stack.Children.Add(labelPlate);
        var root = new Border
        {
            Width = CellW,
            Child = stack,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        string k = kind;
        // 按下必须先截住：否则冒泡到画布启动框选并夺走鼠标捕获，Up 永远到不了 pile
        root.MouseLeftButtonDown += (_, e) => e.Handled = true;
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _expandedStack = _expandedStack == k ? null : k;
            LayoutAll(animated: true);
        };

        IconCanvas.Children.Add(root);
        var pv = new PileVisual
        {
            Root = root, FanGroup = fanGroup, IconFront = iconFront, IconMid = iconMid, IconBack = iconBack,
            CardGroup = cardGroup, Label = label, LabelPlate = labelPlate,
        };
        _stackPiles[kind] = pv;
        ApplyCacheMode(root);

        // 刮擦预览（macOS scrub）= 悬停 + 滚轮/双指滚动轮换前层图标（机主纠正：悬停移动
        // 不该轮换）。零额外取图——成员桌面图标本就常驻加载了 Image.Source，直接借用。
        root.MouseWheel += (_, e) =>
        {
            e.Handled = true; // 别让滚轮漏给画布
            if (pv.Expanded || pv.Members.Count < 2) return;
            int cur = pv.ScrubIndex == -1 ? 0 : pv.ScrubIndex;
            int idx = ((cur + (e.Delta < 0 ? 1 : -1)) % pv.Members.Count + pv.Members.Count) % pv.Members.Count;
            // 防御读：堆成员按 IsDir 快照恒为文件（裸 Image），万一不变式被破也只跳过这帧别崩
            if (pv.Members[idx].IconPlate.Child is not Image frontImg) return;
            pv.ScrubIndex = idx;
            pv.IconFront.Source = frontImg.Source;
            PokeElement(root, 150); // 换图是纯渲染变化，LayoutUpdated 兜不住
        };
        root.MouseLeave += (_, _) =>
        {
            if (pv.ScrubIndex == -1) return;
            pv.ScrubIndex = -1;
            pv.IconFront.Source = pv.RestingFrontIcon;
            PokeElement(root, 150);
        };
        return pv;
    }

    /// <summary>按分组内容与展开态刷新堆的观感（macOS 语义，机主两轮截图对照后定案）：
    /// 收起 = 最多 3 个真实成员图标斜向叠放（含"其他"——收起的堆一律真图标）；
    /// 展开 = 原堆位变成叠层卡片 + 收起箭头（语义占位 = "点此收起"按钮）。
    /// 两态切换带 160ms 交叉渐隐。标签两行"类别\nN 项"。</summary>
    private void UpdatePileVisual(PileVisual pile, string kind, List<IconVisual> members, bool expanded)
    {
        pile.IconMid.Visibility = members.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        pile.IconBack.Visibility = members.Count >= 3 ? Visibility.Visible : Visibility.Collapsed;

        // 布局刷新可能打断进行中的刮擦：先把前层复位到静息图，否则下面 SetLayerIcon 的
        // 路径缓存命中会跳过重载，把刮擦帧误捕获成新的静息图（此后前层永久卡在错的成员上）
        if (pile.ScrubIndex != -1 && pile.RestingFrontIcon != null)
            pile.IconFront.Source = pile.RestingFrontIcon;

        SetLayerIcon(pile.IconFront, ref pile.FrontPath, members[0].Entry.Path);
        if (members.Count >= 2) SetLayerIcon(pile.IconMid, ref pile.MidPath, members[1].Entry.Path);
        if (members.Count >= 3) SetLayerIcon(pile.IconBack, ref pile.BackPath, members[2].Entry.Path);
        pile.RestingFrontIcon = pile.IconFront.Source;

        if (expanded != pile.Expanded)
        {
            // 扇形 ↔ 收起按钮交叉渐隐；两组常驻可视树（IsHitTestVisible 关掉暗侧防误点）
            FadeTo(pile.FanGroup, expanded ? 0 : 1, 160);
            FadeTo(pile.CardGroup, expanded ? 1 : 0, 160);
        }
        else
        {
            pile.FanGroup.Opacity = expanded ? 0 : 1;
            pile.CardGroup.Opacity = expanded ? 1 : 0;
        }
        pile.FanGroup.IsHitTestVisible = !expanded;
        pile.CardGroup.IsHitTestVisible = expanded;

        pile.Members = members;
        pile.Expanded = expanded;
        pile.ScrubIndex = -1;

        pile.Label.Text = $"{kind}\n{members.Count}{L.T(" 项", members.Count == 1 ? " item" : " items")}";
    }

    /// <summary>只在成员路径变化时才重新取图——LayoutStacks 每次重排都会跑到这里，
    /// 裸取图是较贵的 shell COM 往返，缓存路径避免同一张图反复取（尤其三层扇形后 3 倍开销）。</summary>
    private void SetLayerIcon(Image img, ref string? cachedPath, string path)
    {
        if (cachedPath == path) return;
        img.Source = IconLoader.Load(path, IconPx);
        cachedPath = path;
    }

    /// <summary>新图标/整理时的排列顺序：默认名称，_arrangeOrder 指定时按日期/大小/类型。回收站置顶。</summary>
    private IEnumerable<IconVisual> SortForArrange(IEnumerable<IconVisual> src)
    {
        var list = src.ToList();
        var virt = list.Where(i => i.Entry.Path.StartsWith("::"));        // 回收站等虚拟项置顶
        var real = list.Where(i => !i.Entry.Path.StartsWith("::"));
        real = _arrangeOrder switch
        {
            "date" => real.OrderByDescending(FileTime),
            "size" => real.OrderByDescending(FileSize),
            "kind" => real.OrderBy(i => Path.GetExtension(i.Entry.Path).ToLowerInvariant())
                          .ThenBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => real.OrderBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        };
        return virt.Concat(real);
    }

    private static DateTime FileTime(IconVisual iv)
    {
        try { return File.GetLastWriteTime(iv.Entry.Path); } catch { return DateTime.MinValue; }
    }

    private static long FileSize(IconVisual iv)
    {
        try { return new FileInfo(iv.Entry.Path).Length; } catch { return 0; } // 文件夹/取不到 → 0
    }

    /// <summary>一次性排序整理（背景菜单"排序方式"）：所有显示器各自清位重排，一个撤销点。</summary>
    private static void SortArrangeAll(string order)
    {
        LayoutFile.SnapshotForUndo();
        foreach (var w in Desktop.Windows)
        {
            if (!w.Attached) continue;
            LayoutFile.ClearMonitor(w.MonKey);
            foreach (var iv in w._icons.Values) iv.Canon = null;
            w._arrangeOrder = order;
            w.LayoutAll(animated: true);
            w._arrangeOrder = null;
        }
    }

    /// <summary>切换自由摆放/网格模式：持久化后所有窗口重排。网格模式下 LayoutAll 会把连续
    /// Canon 就近吸到格上显示（不回写），切回自由又能回到原连续位置——两模式互不破坏。</summary>
    private static void ToggleFreePlacement()
    {
        Config.FreePlacement = !Config.FreePlacement;
        Config.Save();
        Desktop.LayoutAllWindows(animated: true);
    }

    /// <summary>切换叠放的分组依据（类型/修改日期/大小）：旧分组下展开的堆名字在新分组里
    /// 不存在，LayoutStacks 会自动清空 _expandedStack 并把成员静默重新分堆——不需要额外处理。</summary>
    private static void SetStackGroupBy(string mode)
    {
        if (Config.StackGroupBy == mode) return;
        Config.StackGroupBy = mode;
        Config.Save();
        Desktop.LayoutAllWindows(animated: true);
    }

    // ── 文件夹堆叠（issue #2："以堆叠方式展示"，macOS Dock 文件夹堆叠语义移植到桌面） ──
    // 桌面文件夹可经右键标记：图标带向下角标，叠放模式下单击原地展开内容为真实图标
    // （可开/可拖出/可右键），再点收起；拖文件到文件夹图标上仍是移入。子项是**临时视觉**，
    // 铁律：绝不进 _icons、绝不碰 Canon/布局档——布局档以文件名为键，同名子项会污染桌面
    // 条目；RefreshItems 的存活差集也会把外来键连根清掉。分组的真相源是文件系统本身。

    private sealed class FolderStackView
    {
        public required string FolderPath;
        public readonly List<IconVisual> Children = new();
        public Border? Tile;                 // 尾块：打开文件夹 / 还有 N 项（Dock 网格同款）
        public TextBlock? TileLabel;
        public int HiddenCount;              // 容量截断掉的数量
        public FileSystemWatcher? Watcher;   // 仅展开期间在岗：文件夹内部增删改名 → 刷新
        public DispatcherTimer? RefreshTimer;
    }

    private FolderStackView? _folderStack;   // 每窗口同时最多一个（共用 _expandedStack 单值约束）
    private string? _folderStackPending;     // 展开请求在途（枚举在后台跑）
    private bool _hasStackBadges;            // 有角标挂着：离开叠放模式时 ClearStacks 摘除

    private const string DirKeyPrefix = "dir:"; // 文件夹堆叠在 _expandedStack 里的键前缀
    private static string DirKey(string path) => DirKeyPrefix + path;

    private static bool PathEq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsStackFolder(string path) => Config.StackFolders.Any(f => PathEq(f, path));

    /// <summary>本图标是否按"文件夹堆叠"响应单击（叠放开 + 标记过的桌面文件夹本体）。</summary>
    private static bool IsStackFolderIcon(IconVisual iv) =>
        Config.UseStacks && !iv.StackChild && !iv.Entry.Path.StartsWith("::")
        && IsStackFolder(iv.Entry.Path) && Directory.Exists(iv.Entry.Path);

    /// <summary>按路径切换文件夹堆叠标记（v2 菜单的直达通道：DispatchLocal 手里有被点的
    /// 路径，绕开"哪个窗口有选中"的多屏歧义——跨窗口残留选中会让 SelectionWindow 选错窗）。
    /// 取消标记时若正展开，LayoutStacks 的一致性检查自会收起并摘角标。</summary>
    internal static void ToggleFolderStackFlag(string path)
    {
        if (path.StartsWith("::") || !IsOnDesktop(path) || !Directory.Exists(path)) return;
        int removed = Config.StackFolders.RemoveAll(f => PathEq(f, path));
        if (removed == 0) Config.StackFolders.Add(path);
        Config.Save();
        Log.Write($"folder stack {(removed == 0 ? "on" : "off")}: {path}");
        Desktop.LayoutAllWindows(animated: true);
    }

    /// <summary>旧菜单路径（host 进程 Signal 不带载荷）的兜底：从各窗选中集找目标。
    /// 跨窗残留选中/跨根多选都可能凑出多个候选——只在恰好一个时动手，宁可不做不做错。</summary>
    private static void ToggleFolderStackFromSelection()
    {
        var eligible = Desktop.Windows.SelectMany(w => w._selection)
            .Where(iv => !iv.StackChild && !iv.Entry.Path.StartsWith("::")
                && IsOnDesktop(iv.Entry.Path) && Directory.Exists(iv.Entry.Path))
            .Select(iv => iv.Entry.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (eligible.Count != 1)
        {
            Log.Write($"folder stack toggle skipped: {eligible.Count} eligible folder(s) in selection");
            return;
        }
        ToggleFolderStackFlag(eligible[0]);
    }

    /// <summary>组内已含祖先目录的后代路径剔除（文件夹 + 它展开的子项混拖场景）。</summary>
    private static string[] PruneDescendantPaths(string[] paths) =>
        paths.Length < 2 ? paths
        : paths.Where(p => !paths.Any(q => !ReferenceEquals(q, p)
            && p.Length > q.Length + 1
            && p.StartsWith(q, StringComparison.OrdinalIgnoreCase)
            && (p[q.Length] == '\\' || p[q.Length] == '/'))).ToArray();

    /// <summary>落点是否在展开的文件夹堆叠自己的足迹（子项/尾块格）内——微拖落回原区算取消。</summary>
    private bool IsOverFolderStackArea(Point pos)
    {
        if (_folderStack is not { } v) return false;
        var els = v.Children.Select(c => (FrameworkElement)c.Root).ToList();
        if (v.Tile != null) els.Add(v.Tile);
        foreach (var el in els)
        {
            double l = Canvas.GetLeft(el), t = Canvas.GetTop(el);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double hgt = el.ActualHeight > 0 ? el.ActualHeight : CellH;
            if (pos.X >= l && pos.X < l + CellW && pos.Y >= t && pos.Y < t + hgt) return true;
        }
        return false;
    }

    /// <summary>标记文件夹的单击开关：展开着→收起；请求在途→取消；否则发起展开。</summary>
    private void ToggleFolderStackExpand(string path)
    {
        if (PathEq(_folderStackPending, path)) { _folderStackPending = null; return; } // 在途再点 = 取消
        if (_expandedStack == DirKey(path)) { _expandedStack = null; LayoutAll(animated: true); return; }
        BeginExpandFolderStack(path);
    }

    /// <summary>发起展开：后台枚举（本地文件夹毫秒级；网络重定向/云占位卡住也不冻 UI，
    /// 拿不到就当空文件夹），回 UI 线程呈现。</summary>
    private void BeginExpandFolderStack(string path)
    {
        _folderStackPending = path;
        Task.Run(() =>
        {
            var entries = EnumerateFolderEntries(path);
            Dispatcher.BeginInvoke(() => PresentFolderStack(path, entries));
        });
    }

    /// <summary>文件夹一层内容，过滤口径与桌面枚举一致（隐藏项/desktop.ini 不出镜，快捷方式
    /// 去扩展名显示）。只走一层不递归——junction/symlink 环免疫。</summary>
    private static List<DesktopEntry> EnumerateFolderEntries(string path)
    {
        var result = new List<DesktopEntry>();
        try
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(path))
            {
                var name = Path.GetFileName(p);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (File.GetAttributes(p).HasFlag(FileAttributes.Hidden)) continue; }
                catch { continue; }
                var display = name;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is ".lnk" or ".url") display = Path.GetFileNameWithoutExtension(name);
                result.Add(new DesktopEntry(p, display));
            }
        }
        catch (Exception ex) { Log.Write($"folder stack enumerate failed: {path}: {ex.Message}"); }
        return result;
    }

    /// <summary>展开的文件夹还能吃下几格：总格数 − 单摆项（虚拟+文件夹）− 堆数 − 尾块 1 格。
    /// 与 LayoutStacks 列流同源粗算；放不下的进"还有 N 项"尾块（点开资源管理器，Dock 同款）。
    /// 饼堆展开本就可能溢屏，但文件夹能装上千项，把溢出从理论病变成必然病——必须设限。</summary>
    private int FolderStackCapacity()
    {
        int total = (MaxCol() + 1) * RowsPerColumn();
        int singles = _icons.Values.Count(i => i.Entry.Path.StartsWith("::") || i.IsDir);
        var (classify, _) = StackGrouping();
        int piles = _icons.Values
            .Where(i => !i.Entry.Path.StartsWith("::") && !i.IsDir)
            .Select(i => classify(i.Entry)).Distinct().Count();
        return Math.Max(0, total - singles - piles - 1);
    }

    /// <summary>枚举回来后的呈现：建临时子项图标（初始停在文件夹位、微透明，LayoutStacks
    /// 展开分支弹飞+渐显）+ 尾块 + 展开期 watcher，然后交给 LayoutAll 统一驱动。</summary>
    private void PresentFolderStack(string path, List<DesktopEntry> entries)
    {
        if (!PathEq(_folderStackPending, path)) return; // 等待期间被取消/换人
        _folderStackPending = null;
        if (!Config.UseStacks || !IsStackFolder(path)) return; // 世界变了：不展开
        var folderIv = _icons.Values.FirstOrDefault(i => PathEq(i.Entry.Path, path));
        if (folderIv == null) return; // 文件夹已不归本窗口

        if (_folderStack is { } old) { _expandedStack = null; CollapseFolderStack(old, animated: true); } // 换台先收旧的

        double fl = Canvas.GetLeft(folderIv.Root), ft = Canvas.GetTop(folderIv.Root);
        if (double.IsNaN(fl) || double.IsNaN(ft)) { fl = 0; ft = 0; }

        var view = new FolderStackView { FolderPath = path };
        var sorted = entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        int show = Math.Min(sorted.Count, FolderStackCapacity());
        foreach (var en in sorted.Take(show))
            AddFolderStackChild(view, en, fl, ft);
        view.HiddenCount = sorted.Count - show;
        MakeFolderStackTile(view);
        Canvas.SetLeft(view.Tile!, fl);
        Canvas.SetTop(view.Tile!, ft);
        view.Tile!.Opacity = 0.2;
        view.Tile.IsHitTestVisible = false; // 孵化期禁命中，同子项（见 ArmFolderStackHitTest）
        IconCanvas.Children.Add(view.Tile);

        // 展开期哨兵：桌面 watcher 看不见文件夹内部，shell 菜单删除/外部增删靠它刷新
        view.RefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        view.RefreshTimer.Tick += (_, _) =>
        {
            view.RefreshTimer!.Stop();
            if (!ReferenceEquals(_folderStack, view)) return;
            RefreshFolderStack(view);
            if (view.Watcher == null)
            {
                // watcher 没立起来（网络重定向桌面等）：轮询兜底，别让展开视图冻死
                view.RefreshTimer.Interval = TimeSpan.FromMilliseconds(2000);
                view.RefreshTimer.Start();
            }
        };
        try
        {
            var w = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            void Poke() => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_folderStack, view)) return;
                view.RefreshTimer!.Stop();
                view.RefreshTimer.Start();
            });
            w.Created += (_, _) => Poke();
            w.Deleted += (_, _) => Poke();
            w.Renamed += (_, _) => Poke();
            view.Watcher = w;
        }
        catch (Exception ex) { Log.Write("folder stack watcher failed: " + ex.Message); }

        _folderStack = view;
        _expandedStack = DirKey(path);
        Log.Write($"folder stack expand: {path} ({view.Children.Count} shown, +{view.HiddenCount} more)");
        LayoutAll(animated: true);
        ArmFolderStackHitTest(view);
        LoadFolderStackIconsAsync(view);
        view.RefreshTimer.Start(); // 布防补刷：枚举快照与 watcher 上岗之间落进来的变化补一轮
    }

    /// <summary>取图标 Image 元素（文件 = 裸 Image，文件夹 = Grid 包装的第一层）。</summary>
    private static Image? IconImageOf(IconVisual iv) =>
        iv.IconPlate.Child as Image ?? (iv.IconPlate.Child as Grid)?.Children.OfType<Image>().FirstOrDefault();

    /// <summary>后台顺序补齐子项图标（deferIcon 的下半场）：shell 取图在 MTA 线程照跑，
    /// 拉到一张回 UI 填一张（渐进出现，Dock 网格同款手感）。视图收起即停。</summary>
    private void LoadFolderStackIconsAsync(FolderStackView view)
    {
        var work = view.Children
            .Where(c => IconImageOf(c) is { Source: null })
            .Select(c => (Icon: c, c.Entry.Path)).ToList();
        if (work.Count == 0) return;
        int px = IconPx;
        Task.Run(() =>
        {
            foreach (var (civ, path) in work)
            {
                if (!ReferenceEquals(_folderStack, view)) return; // 已收起/换台：别白拉
                var src = IconLoader.Load(path, px);
                if (src == null) continue;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_folderStack, view)) return;
                    if (IconImageOf(civ) is { Source: null } im)
                    {
                        im.Source = src;
                        PokeElement(civ.Root, 150); // 换图纯渲染变化，动态模式要自己催帧
                    }
                });
            }
        });
    }

    private void AddFolderStackChild(FolderStackView view, DesktopEntry en, double fromL, double fromT)
    {
        var civ = CreateIconVisual(en, deferIcon: true);
        civ.StackChild = true;
        civ.Root.Opacity = 0.2;              // LayoutStacks 展开分支渐显到全显
        civ.Root.IsHitTestVisible = false;   // 孵化期禁命中（见 ArmFolderStackHitTest）
        Canvas.SetLeft(civ.Root, fromL);     // 先停在文件夹位：MoveElement 有起点才有飞出动画
        Canvas.SetTop(civ.Root, fromT);
        IconCanvas.Children.Add(civ.Root);
        view.Children.Add(civ);
    }

    /// <summary>孵化期禁命中的解除：子项/尾块刚孵化时还整摞叠在文件夹位上，双击的第二击
    /// 会正好砸在最上层的子项上（误启动子文件/误开资源管理器）——飞行到位后才开放交互。</summary>
    private void ArmFolderStackHitTest(FolderStackView view)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ExpandMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!ReferenceEquals(_folderStack, view)) return; // 期间已收起：弥留元素保持惰性
            foreach (var c in view.Children) c.Root.IsHitTestVisible = true;
            if (view.Tile != null) view.Tile.IsHitTestVisible = true;
        };
        t.Start();
    }

    /// <summary>展开子项 + 尾块的列流落位（LayoutStacks 单摆循环内调用，紧跟文件夹本体）。</summary>
    private void LayoutFolderStackChildren(FolderStackView view, Func<(double L, double T)> take, bool animated)
    {
        foreach (var c in view.Children)
        {
            var (l, t) = take();
            MoveElement(c.Root, l, t, animated, EaseSpringOut, ExpandMs);
            if (c.Root.Opacity < 0.99 && !_dragGhosts.Contains(c) && !_cutIcons.Contains(c))
                FadeTo(c.Root, 1, 200);
        }
        if (view.Tile is { } tile)
        {
            var (l, t) = take();
            MoveElement(tile, l, t, animated, EaseSpringOut, ExpandMs);
            if (tile.Opacity < 0.99) FadeTo(tile, 1, 200);
        }
    }

    /// <summary>收起并销毁文件夹堆叠视图：子项飞回文件夹位渐隐，播完出画布；watcher 立即下岗。
    /// 临时子项可能残留在选择/剪切/焦点/拖影集里，此处一并清（RefreshItems 只清 _icons 的）。</summary>
    private void CollapseFolderStack(FolderStackView view, bool animated)
    {
        if (ReferenceEquals(_folderStack, view)) _folderStack = null;
        try { view.Watcher?.Dispose(); } catch { }
        view.Watcher = null;
        view.RefreshTimer?.Stop();

        var folderIv = _icons.Values.FirstOrDefault(i => PathEq(i.Entry.Path, view.FolderPath));
        double hl = 0, ht = 0;
        bool haveHome = folderIv != null && !double.IsNaN(Canvas.GetLeft(folderIv.Root));
        if (haveHome) { hl = Canvas.GetLeft(folderIv!.Root); ht = Canvas.GetTop(folderIv.Root); }

        var dying = new List<FrameworkElement>();
        foreach (var c in view.Children)
        {
            _selection.Remove(c);
            if (_focusIcon == c) _focusIcon = null;
            _dragGhosts.Remove(c);
            _cutIcons.Remove(c);
            if (_renaming == c) CancelRename();
            dying.Add(c.Root);
        }
        view.Children.Clear();
        if (view.Tile != null) dying.Add(view.Tile);

        if (!animated)
        {
            foreach (var el in dying) DiscardStackVisual(el);
            return;
        }
        foreach (var el in dying)
        {
            el.IsHitTestVisible = false; // 弥留期不接鼠标：渐隐中被点会变成隐形僵尸选中/误开尾块
            if (haveHome) MoveElement(el, hl, ht, true, EaseInhale, CollapseMs); // 文件夹已无踪则原地淡出
            FadeTo(el, 0, CollapseMs - 120, 120); // 后段渐隐，与饼堆收起同手感
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var el in dying) DiscardStackVisual(el); // 视图已脱钩，无条件出画布（重开是新视图）
        };
        timer.Start();
    }

    /// <summary>临时视觉出画布前的销账：动态壁纸+去阴影模式下 StripEffects 按元素记了账，
    /// 字典只在退出动态模式时整体清空——不还账就随每轮展开/收起泄漏（连带钉死整棵可视树）。</summary>
    private void DiscardStackVisual(FrameworkElement el)
    {
        RestoreEffects(el);
        IconCanvas.Children.Remove(el);
    }

    /// <summary>展开期间文件夹内部变了：重枚举做增量补删，排序/尾块计数跟着走。</summary>
    private void RefreshFolderStack(FolderStackView view)
    {
        string path = view.FolderPath;
        Task.Run(() =>
        {
            var entries = EnumerateFolderEntries(path);
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_folderStack, view)) return;
                var alive = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var c in view.Children.Where(c => !alive.Contains(c.Entry.Path)).ToList())
                {
                    DiscardStackVisual(c.Root);
                    _selection.Remove(c);
                    if (_focusIcon == c) _focusIcon = null;
                    _dragGhosts.Remove(c);
                    _cutIcons.Remove(c);
                    if (_renaming == c) CancelRename();
                    view.Children.Remove(c);
                }
                var have = new HashSet<string>(view.Children.Select(c => c.Entry.Path), StringComparer.OrdinalIgnoreCase);
                var folderIv = _icons.Values.FirstOrDefault(i => PathEq(i.Entry.Path, path));
                double fl = 0, ft = 0;
                if (folderIv != null && !double.IsNaN(Canvas.GetLeft(folderIv.Root)))
                { fl = Canvas.GetLeft(folderIv.Root); ft = Canvas.GetTop(folderIv.Root); }
                int cap = FolderStackCapacity();
                foreach (var en in entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (view.Children.Count >= cap) break;
                    if (have.Contains(en.Path)) continue;
                    AddFolderStackChild(view, en, fl, ft);
                }
                view.Children.Sort((a, b) => string.Compare(a.Entry.DisplayName, b.Entry.DisplayName,
                    StringComparison.CurrentCultureIgnoreCase));
                view.HiddenCount = Math.Max(0, entries.Count - view.Children.Count);
                UpdateFolderStackTileLabel(view);
                LayoutAll(animated: true);
                ArmFolderStackHitTest(view); // 补进来的新子项也走孵化期禁命中（对老成员置 true 幂等）
                LoadFolderStackIconsAsync(view);
            });
        });
    }

    /// <summary>尾块：圆环转出箭头 + "打开文件夹 / 还有 N 项"（Dock 网格的 Open in Finder
    /// 同款，机主截图样式）。单击展开取代了双击开窗，开资源管理器的入口保留在这。</summary>
    private void MakeFolderStackTile(FolderStackView view)
    {
        var arrow = new System.Windows.Shapes.Path
        {
            // 弯柄转出箭头。整数坐标 + LayoutTransform 缩放，避开路径迷你语言的小数分隔符本地化坑
            Data = Geometry.Parse("M 3,17 C 3,8 9,4 15,4 L 15,0 L 23,7 L 15,14 L 15,9 C 10,9 7,12 7,17 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // 视觉配重：箭头质心在右侧的实心箭头上（弯柄纤细无分量），包围盒居中会
            // 整体偏右下、左上留白刺眼（机主点名）——右/下外边距把它往左上挪回光学中心
            Margin = new Thickness(0, 0, 6 * S, 3 * S),
            LayoutTransform = S == 1.0 ? Transform.Identity : new ScaleTransform(S, S),
        };
        var ring = new Border
        {
            Width = 40 * S, Height = 40 * S,
            CornerRadius = new CornerRadius(20 * S),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(2.5 * S),
            Child = arrow,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var card = new Border
        {
            Width = 58 * S, Height = 58 * S,
            CornerRadius = new CornerRadius(12 * S),
            Background = new SolidColorBrush(Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = ring,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plate = new Grid { Width = 76 * S, Height = 74 * S, HorizontalAlignment = HorizontalAlignment.Center };
        plate.Children.Add(card);

        var label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12 * S,
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.85 },
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        var labelPlate = new Border
        {
            CornerRadius = new CornerRadius(6 * S),
            Padding = new Thickness(5 * S, 1, 5 * S, 2),
            Background = Brushes.Transparent,
            Child = label,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel();
        stack.Children.Add(plate);
        stack.Children.Add(labelPlate);
        var root = new Border
        {
            Width = CellW,
            Child = stack,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        string folder = view.FolderPath;
        // 按下先截住：漏给画布会启动框选夺走捕获，Up 永远到不了（饼堆同款坑）
        root.MouseLeftButtonDown += (_, e) => e.Handled = true;
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false }); }
            catch (Exception ex) { Log.Write("folder stack tile open failed: " + ex.Message); }
        };
        view.Tile = root;
        view.TileLabel = label;
        UpdateFolderStackTileLabel(view);
        ApplyCacheMode(root);
    }

    private void UpdateFolderStackTileLabel(FolderStackView view)
    {
        if (view.TileLabel == null) return;
        view.TileLabel.Text = view.HiddenCount > 0
            ? L.T($"还有 {view.HiddenCount} 项", view.HiddenCount == 1 ? "1 More" : $"{view.HiddenCount} More")
            : L.T("打开文件夹", "Open in Folder");
    }

    /// <summary>堆叠文件夹角标（右下小圆盘 + V 箭头，展开时翻转朝上）。挂在 CreateIconVisual
    /// 给文件夹包的 Grid 里；文件成员是裸 Image 进不来（也不该来——饼堆 scrub 强转靠此约）。</summary>
    private void UpdateStackBadge(IconVisual iv, bool show, bool expanded)
    {
        if (iv.IconPlate.Child is not Grid wrap) return; // 非文件夹：无处安放
        if (!show)
        {
            if (iv.StackBadge == null) return;
            wrap.Children.Remove(iv.StackBadge);
            iv.StackBadge = null;
            iv.StackChevronRot = null;
            PokeElement(iv.Root, 120);
            return;
        }
        bool fresh = iv.StackBadge == null;
        if (fresh)
        {
            var chevron = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 6,5 L 12,0"), // 整数坐标 + LayoutTransform，同饼堆箭头的本地化防坑
                Stroke = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // 居中三段修（机主点名不齐，真机像素质心迭代实测）：①去掉照抄饼堆的
                // 2S 下沉（小圆里过矫 ~5px）②角标关布局取整（整像素吸附偏 ~1px）
                // ③残余 ~0.5 DIU 左上系统偏差用平移精确配平——RenderTransform 随角标
                // 旋转，∨/∧ 两态同时归零（Margin/LayoutTransform 做不到这点）
                RenderTransform = new TranslateTransform(0.67 * S, 0.67 * S),
                LayoutTransform = S == 1.0 ? Transform.Identity : new ScaleTransform(S, S),
            };
            var rot = new RotateTransform(0);
            var badge = new Border
            {
                // 关布局取整：窗口级 UseLayoutRounding 会把箭头的排布矩形吸到整像素，
                // 在 21S 的小圆里偏出 ~2px 且随 180° 旋转镜像放大（真机像素质心实测）；
                // 亚像素居中 + 抗锯齿在 5px 笔画上不可感，几何居中在任意 DPI 精确成立
                UseLayoutRounding = false,
                Width = 21 * S, Height = 21 * S,
                CornerRadius = new CornerRadius(10.5 * S),
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1C, 0x1C, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = chevron,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = rot,
                RenderTransformOrigin = new Point(0.5, 0.5),
                IsHitTestVisible = false, // 命中交给整图标：单击 = 展开/收起
            };
            iv.StackBadge = badge;
            iv.StackChevronRot = rot;
            wrap.Children.Add(badge);
            _hasStackBadges = true;
        }
        double want = expanded ? 180 : 0;
        if (fresh || iv.StackChevronRot!.Angle != want)
        {
            iv.StackChevronRot!.Angle = want;
            PokeElement(iv.Root, 120); // 纯渲染变化，LayoutUpdated 兜不住
        }
    }

    private static void Advance(ref int col, ref int row, int rows)
    {
        row++;
        if (row >= rows) { row = 0; col++; }
    }

    private (int, int) PosToCell(double l, double t)
    {
        var (w, _) = WorkSize;
        int col = (int)Math.Round((w - MarginRight - CellW - l) / (CellW + GapX));
        int row = (int)Math.Round((t - GridTop) / (CellH + GapY));
        return (col, row);
    }

    // ── 动画手感（macOS 观感，Finder 手感调研没有像素级曲线数据，按观感定）──
    // 通用重排 = 指数缓出（前段快后段极缓，比 CubicEase 柔和得多，接近 mac 的 spring 收尾）；
    // 堆展开 = 轻微过冲的 BackEase（弹簧"甩出来"的灵魂）；堆收起 = 缓入（加速被吸进堆里）。
    private static readonly IEasingFunction EaseGlide = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
    private static readonly IEasingFunction EaseSpringOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
    private static readonly IEasingFunction EaseInhale = new CubicEase { EasingMode = EasingMode.EaseIn };
    private const int GlideMs = 400, ExpandMs = 430, CollapseMs = 300;

    private static void MoveIcon(IconVisual iv, double l, double t, bool animated) =>
        MoveElement(iv.Root, l, t, animated, EaseGlide, GlideMs);

    private static void MoveElement(FrameworkElement el, double l, double t, bool animated,
        IEasingFunction ease, int ms)
    {
        // 落点吸附整数 DIU：亚像素坐标会让整个图标（尤其文字）渲染发糊
        l = Math.Round(l);
        t = Math.Round(t);
        if (animated && AnimationsSuppressed(el)) animated = false;
        if (animated && !double.IsNaN(Canvas.GetLeft(el)))
        {
            var ax = new DoubleAnimation(l, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            var ay = new DoubleAnimation(t, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            el.BeginAnimation(Canvas.LeftProperty, ax);
            el.BeginAnimation(Canvas.TopProperty, ay);
            PokeWindowOf(el, ms + 150); // 位移动画全程催帧（LayoutUpdated 只负责唤醒不负责续命）
        }
        else
        {
            el.BeginAnimation(Canvas.LeftProperty, null);
            el.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(el, l);
            Canvas.SetTop(el, t);
            PokeWindowOf(el, 80);
        }
    }

    /// <summary>透明度过渡（机主点名的 mac 细节：图标收进堆"即将消失时"要有渐隐，
    /// 否则到位后突然消失很生硬；展开反向渐显）。delayMs 让渐隐集中在行程后段。</summary>
    private static void FadeTo(FrameworkElement el, double to, int ms, int delayMs = 0)
    {
        if (AnimationsSuppressed(el))
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = to;
            PokeWindowOf(el, 80);
            return;
        }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        el.BeginAnimation(OpacityProperty, a);
        PokeWindowOf(el, delayMs + ms + 150); // 透明度动画不触发布局，按动画时长催帧
    }

    /// <summary>清掉透明度动画并复位到全显——展开被收起中的成员、或隐藏完成后归位用。</summary>
    private static void ResetOpacity(FrameworkElement el)
    {
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = 1;
        PokeWindowOf(el, 100);
    }

    // ── 选择模型 ──────────────────────────────────────────────

    private static readonly SolidColorBrush SelIconBg = new(Color.FromArgb(0x52, 0x00, 0x00, 0x00));
    private static SolidColorBrush SelLabelBg => Accent.LabelBrush; // 强调色（设置里可换，默认 mac 选中蓝）

    private void SetSelected(IconVisual iv, bool on)
    {
        if (on) _selection.Add(iv); else _selection.Remove(iv);
        iv.IconPlate.Background = on ? SelIconBg : Brushes.Transparent;
        iv.LabelPlate.Background = on ? SelLabelBg : Brushes.Transparent;
        PokeElement(iv.Root, 120); // 笔刷变化是纯渲染，透传态要催帧
    }

    private void ClearSelection()
    {
        foreach (var iv in _selection.ToList()) SetSelected(iv, false);
    }

    private void SelectOnly(IconVisual iv)
    {
        ClearSelection();
        SetSelected(iv, true);
        _focusIcon = iv;
    }

    private void FocusDesktop()
    {
        // 先同步把桌面顶层（Progman 链）请到前台。不这么做的话，本次点击引发的**异步**
        // 激活会在右键菜单开出之后才落地，把菜单扫掉——"从别的应用切过来后第一下右键
        // 时灵时不灵"的根源。先到位，后面就没有迟到的激活了。
        Native.SetForegroundWindow(Native.GetAncestor(_hwnd, Native.GA_ROOT));
        Native.SetFocus(_hwnd);
        Keyboard.Focus(this);
    }

    /// <summary>
    /// 右键菜单前的前台准备：只请桌面链到前台，**不做 SetFocus/Keyboard.Focus**——
    /// 真机日志实锤：WPF 拿焦点会让我们自己的窗口在 ~85ms 后异步夺前台，把菜单
    /// host 刚开的菜单扫掉（cls=HwndWrapper[MacDesk] 的 insta-cancel 元凶就是自己人）。
    /// 菜单不需要我们这边的键盘焦点。
    /// </summary>
    private void PrepareForMenu() =>
        Native.SetForegroundWindow(Native.GetAncestor(_hwnd, Native.GA_ROOT));

    // 右键按下点快照：按住时的轻微移动不应改变菜单目标/位置（按下压着谁就对谁弹菜单，
    // 菜单钉在按下点；否则微移后在空白处抬起会误出背景菜单）
    private (Point Pos, IconVisual? Icon, DateTime At) _rightPress;

    /// <summary>参与交互的图标（叠放模式下堆内未展开项 Collapsed，不参与命中/选择/导航）。</summary>
    private IEnumerable<IconVisual> VisibleIcons => _icons.Values.Where(i => i.Root.Visibility == Visibility.Visible);

    private IconVisual? IconAtPoint(Point pos)
    {
        // 展开的文件夹堆叠子项也参与命中（右键快照用），且优先——它们后加入画布、绘在上层，
        // 展开/收起动画瞬间与桌面图标重叠时以视觉上层为准
        var children = _folderStack?.Children ?? (IEnumerable<IconVisual>)Array.Empty<IconVisual>();
        foreach (var iv in children.Concat(VisibleIcons))
        {
            double l = Canvas.GetLeft(iv.Root), t = Canvas.GetTop(iv.Root);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double hgt = iv.Root.ActualHeight > 0 ? iv.Root.ActualHeight : CellH;
            if (pos.X >= l && pos.X < l + CellW && pos.Y >= t && pos.Y < t + hgt) return iv;
        }
        return null;
    }

    private bool RightPressFresh => (DateTime.Now - _rightPress.At).TotalMilliseconds < 800;

    // ── 图标鼠标交互（统一 shell OLE 拖拽） ────────────────────

    private readonly HashSet<IconVisual> _dragGhosts = new(); // 拖拽中留影的图标（自愈时豁免）

    /// <summary>
    /// 进程内拖拽会话：OLE 数据对象带不了抓取偏移，Drop 端从这里拿。
    /// 有它才能做到"从哪里抓起、就落回视觉所在的位置"（否则每次放下都平移一个抓取偏移，
    /// 机主实测像在对齐一个看不见的网格）。静态 = 跨窗口（跨屏拖拽）可见。
    /// </summary>
    internal sealed record DragContext(string AnchorPath, Point GrabOffset, Dictionary<string, Point> RelOffsets);

    internal static DragContext? ActiveDrag;

    private void OnIconMouseDown(IconVisual iv, MouseButtonEventArgs e)
    {
        e.Handled = true;
        FocusDesktop();
        MenuHost.Dismiss(); // 兜底收掉可能残留的菜单
        CommitRename(); // 点别处 = 提交重命名（mac 行为）

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl)
        {
            SetSelected(iv, !_selection.Contains(iv));
        }
        else if (!_selection.Contains(iv))
        {
            SelectOnly(iv); // 已选中的不动，支持组拖拽
        }
        _focusIcon = iv; // 键盘导航锚点

        if (e.ClickCount == 2)
        {
            // 堆叠文件夹：单击已负责展开/收起，双击不再另开资源管理器（Dock 堆叠无双击语义，
            // 开窗走展开视图尾块或右键"打开"）。不设 MouseDown，第二击的 Up 不会再触发展开
            if (!IsStackFolderIcon(iv)) OpenEntry(iv.Entry);
            return;
        }

        iv.MouseDown = true;
        iv.Dragging = false;
        iv.DownPos = e.GetPosition(IconCanvas);
        iv.Root.CaptureMouse();
    }

    private void OnIconMouseMove(IconVisual iv, MouseEventArgs e)
    {
        if (!iv.MouseDown || iv.Dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // up 事件被别的窗口/菜单吃掉的残留按下态：清零，绝不能拿松开的鼠标开拖拽
            iv.MouseDown = false;
            if (iv.Root.IsMouseCaptured) iv.Root.ReleaseMouseCapture();
            return;
        }
        var p = e.GetPosition(IconCanvas);
        if (Math.Abs(p.X - iv.DownPos.X) <= 4 && Math.Abs(p.Y - iv.DownPos.Y) <= 4) return;

        // 过阈值 → 直接进 shell OLE 拖拽（Explorer/Finder 同款）：拖拽图像由 shell 分层窗口
        // 渲染，隔窗、跨屏、长距离都稳定跟手；落回自己/兄弟屏窗口由 Drop 重定位，
        // 落到资源管理器/外部应用是真文件拖放。取代旧的"手动拖 + WindowFromPoint 切 OLE"双轨制。
        iv.Dragging = true;
        var group = _selection.Contains(iv) && _selection.Count > 1
            ? _selection.ToList() : new List<IconVisual> { iv };
        var allPaths = group.Select(g => g.Entry.Path).ToArray();
        var filePaths = allPaths.Where(path => !path.StartsWith("::")).ToArray();

        iv.MouseDown = false;
        iv.Root.ReleaseMouseCapture();

        // 抓取基准一律用按下点（不是过阈值那一刻的光标）：拖拽图像出现时正好覆盖原图标，
        // 落位 = 图像视觉位置，拿起-放回零漂移
        var (image, hotspot) = RenderDragImage(group, iv.DownPos);
        double ax = double.IsNaN(Canvas.GetLeft(iv.Root)) ? 0 : Canvas.GetLeft(iv.Root);
        double ay = double.IsNaN(Canvas.GetTop(iv.Root)) ? 0 : Canvas.GetTop(iv.Root);
        ActiveDrag = new DragContext(iv.Entry.Path, new Point(iv.DownPos.X - ax, iv.DownPos.Y - ay),
            group.ToDictionary(g => g.Entry.Path, g => new Point(
                (double.IsNaN(Canvas.GetLeft(g.Root)) ? 0 : Canvas.GetLeft(g.Root)) - ax,
                (double.IsNaN(Canvas.GetTop(g.Root)) ? 0 : Canvas.GetTop(g.Root)) - ay),
                StringComparer.OrdinalIgnoreCase));
        // 组包围盒左上角（回弹动画的归位点；拖拽图像的原点与此对应）
        double homeX = group.Min(g => double.IsNaN(Canvas.GetLeft(g.Root)) ? 0 : Canvas.GetLeft(g.Root));
        double homeY = group.Min(g => double.IsNaN(Canvas.GetTop(g.Root)) ? 0 : Canvas.GetTop(g.Root));

        foreach (var g in group) { g.Root.Opacity = 0.35; _dragGhosts.Add(g); PokeElement(g.Root, 200); } // 原位留影
        DragDropEffects? result = null;
        try { result = ShellDrag.Start(filePaths, allPaths, image, hotspot); }
        catch (Exception ex) { Log.Write("drag failed: " + ex.Message); }
        finally
        {
            ActiveDrag = null;
            iv.Dragging = false;
        }
        if (result == null)
        {
            // 取消（Esc/右键/无效落点）：拖拽图像从取消点飞回原位（Finder 手感），
            // 动画落地才恢复原图标透明度（期间 _dragGhosts 豁免自愈）
            SpringBack(group, image, hotspot, new Point(homeX, homeY));
        }
        else
        {
            foreach (var g in group) { g.Root.Opacity = 1; PokeElement(g.Root, 150); }
            _dragGhosts.Clear();
        }
        // 后续：拖回自己/兄弟窗口 → OnDesktopDrop 重定位；Move 去外部 → FileSystemWatcher 移除图标
    }

    /// <summary>拖拽取消回弹：用拖拽位图做幽灵，从当前光标位置飞回组原位。</summary>
    private void SpringBack(List<IconVisual> group, BitmapSource imagePx, Native.POINT hotspotPx, Point homeTL)
    {
        double dev = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double k = (RootGrid.LayoutTransform as ScaleTransform)?.ScaleX ?? 1.0;
        double scale = dev * k;

        Point cur = homeTL;
        try
        {
            Native.GetCursorPos(out var cp);
            var p = IconCanvas.PointFromScreen(new Point(cp.X, cp.Y));
            cur = new Point(p.X - hotspotPx.X / scale, p.Y - hotspotPx.Y / scale);
        }
        catch { }

        var ghost = new Image
        {
            Source = imagePx,
            Width = imagePx.PixelWidth / scale,
            Height = imagePx.PixelHeight / scale,
            IsHitTestVisible = false,
            Opacity = 0.9,
        };
        System.Windows.Controls.Panel.SetZIndex(ghost, 2000);
        IconCanvas.Children.Add(ghost);
        Canvas.SetLeft(ghost, cur.X);
        Canvas.SetTop(ghost, cur.Y);

        void Land()
        {
            IconCanvas.Children.Remove(ghost);
            foreach (var g in group) { g.Root.Opacity = 1; PokeElement(g.Root, 150); }
            _dragGhosts.Clear();
        }
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ax = new DoubleAnimation(cur.X, homeTL.X, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(cur.Y, homeTL.Y, dur) { EasingFunction = ease };
        ax.Completed += (_, _) => Land();
        ghost.BeginAnimation(Canvas.LeftProperty, ax);
        ghost.BeginAnimation(Canvas.TopProperty, ay);
        PokeElement(ghost, 400); // 回弹全程催帧（脏区跟着幽灵飞）
    }

    /// <summary>拖拽图像：组图标按当前相对位置合成；包围盒过大退化为锚点图标+计数角标。物理像素。</summary>
    private (BitmapSource Image, Native.POINT Hotspot) RenderDragImage(List<IconVisual> group, Point cursor)
    {
        double dev = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double k = (RootGrid.LayoutTransform as ScaleTransform)?.ScaleX ?? 1.0;
        double scale = dev * k;

        static double L(IconVisual g) => double.IsNaN(Canvas.GetLeft(g.Root)) ? 0 : Canvas.GetLeft(g.Root);
        static double T(IconVisual g) => double.IsNaN(Canvas.GetTop(g.Root)) ? 0 : Canvas.GetTop(g.Root);
        double H(IconVisual g) => g.Root.ActualHeight > 0 ? g.Root.ActualHeight : CellH;

        double minX = group.Min(L), minY = group.Min(T);
        double w = group.Max(g => L(g) + CellW) - minX;
        double h = group.Max(g => T(g) + H(g)) - minY;

        var dv = new DrawingVisual();
        if (w <= 560 && h <= 560)
        {
            using var dc = dv.RenderOpen();
            dc.PushOpacity(0.85);
            foreach (var g in group)
                dc.DrawRectangle(new VisualBrush(g.Root), null, new Rect(L(g) - minX, T(g) - minY, CellW, H(g)));
            dc.Pop();
        }
        else
        {
            // 稀疏大组：只画锚点图标 + 计数角标
            var anchor = group[0];
            minX = L(anchor) - 12;
            minY = T(anchor) - 12;
            w = CellW + 24;
            h = H(anchor) + 24;
            using var dc = dv.RenderOpen();
            dc.PushOpacity(0.85);
            dc.DrawRectangle(new VisualBrush(anchor.Root), null, new Rect(12, 12, CellW, H(anchor)));
            dc.Pop();
            var center = new Point(12 + CellW - 4, 16);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)), null, center, 13, 13);
            var txt = new FormattedText(group.Count.ToString(), System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(txt, new Point(center.X - txt.Width / 2, center.Y - txt.Height / 2));
        }

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var hotspot = new Native.POINT
        {
            X = Math.Clamp((int)((cursor.X - minX) * scale), 0, rtb.PixelWidth - 1),
            Y = Math.Clamp((int)((cursor.Y - minY) * scale), 0, rtb.PixelHeight - 1),
        };
        return (rtb, hotspot);
    }

    private void OnIconMouseUp(IconVisual iv, MouseButtonEventArgs e)
    {
        if (!iv.MouseDown) return;
        iv.MouseDown = false;
        iv.Root.ReleaseMouseCapture();
        // 文件夹堆叠：干净单击（没拖成、非 Ctrl 多选手势）= 原地展开/收起（与饼堆单击同语义）。
        // 拖拽路径在过阈值时已把 MouseDown 清零并释放捕获，走不到这里
        if (!iv.Dragging && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && IsStackFolderIcon(iv))
            ToggleFolderStackExpand(iv.Entry.Path);
    }

    /// <summary>目标格被占时找最近空格（按网格距离环形扩散）。</summary>
    private (int, int) NearestFreeCell((int, int) want, HashSet<(int, int)> occupied)
    {
        if (!occupied.Contains(want)) return want;
        int maxCol = MaxCol();
        int rows = RowsPerColumn();
        for (int radius = 1; radius <= maxCol + rows; radius++)
        {
            var candidates = new List<(int c, int r)>();
            for (int dc = -radius; dc <= radius; dc++)
                for (int dr = -radius; dr <= radius; dr++)
                {
                    if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius) continue;
                    int c = want.Item1 + dc, r = want.Item2 + dr;
                    if (c < 0 || c > maxCol || r < 0 || r >= rows) continue;
                    if (!occupied.Contains((c, r))) candidates.Add((c, r));
                }
            if (candidates.Count > 0)
                return candidates.OrderBy(x =>
                    Math.Pow(x.c - want.Item1, 2) + Math.Pow(x.r - want.Item2, 2)).First();
        }
        return want; // 全满：接受重叠
    }

    /// <summary>按下点近旁抬起（≤16 DIU）→ 菜单钉在按下点；拖远了（右键拖拽）→ 用抬起点。</summary>
    private Point MenuAnchor(Point upPos) =>
        RightPressFresh && (upPos - _rightPress.Pos).Length <= 16 ? _rightPress.Pos : upPos;

    private bool PressNear(Point upPos) =>
        RightPressFresh && (upPos - _rightPress.Pos).Length <= 16;

    private void OnIconRightClick(IconVisual iv, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var up = e.GetPosition(IconCanvas);
        // 按下时压着别的图标（按住轻微移动跨图标）→ 以按下时的为准
        if (PressNear(up) && _rightPress.Icon != null) iv = _rightPress.Icon;
        ShowIconMenu(iv, MenuAnchor(up));
    }

    private void ShowIconMenu(IconVisual iv, Point canvasAnchor)
    {
        PrepareForMenu();
        if (!_selection.Contains(iv)) SelectOnly(iv);
        var pt = IconCanvas.PointToScreen(canvasAnchor); // 物理 px

        // 多选且同属一个父目录 → 合并菜单；否则只对点中的那个
        var sel = _selection.Select(s => s.Entry.Path).ToList();
        string[] paths;
        if (sel.Count > 1 && !sel.Any(p => p.StartsWith("::"))
            && sel.Select(Path.GetDirectoryName).Distinct().Count() == 1)
            paths = sel.ToArray();
        else
            paths = new[] { iv.Entry.Path };

        MenuHost.RequestFileMenu((int)pt.X, (int)pt.Y, paths, _hwnd);
    }

    private static void OpenEntry(DesktopEntry en)
    {
        try
        {
            if (en.Path == DesktopItemProvider.RecycleBin)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "shell:RecycleBinFolder", UseShellExecute = true });
                return;
            }
            if (en.Path.StartsWith("::"))
            {
                // 其他虚拟命名空间项（此电脑/网络/控制面板/用户文件）：交给资源管理器解析
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{en.Path}\"", UseShellExecute = true });
                return;
            }
            Process.Start(new ProcessStartInfo(en.Path) { UseShellExecute = true });
        }
        catch { /* 打不开就算了，别崩 */ }
    }

    // ── 菜单命令（背景菜单子进程经 CommandChannel 触发，作用于所有窗口） ──────

    private static void ArrangeAllWithUndo()
    {
        LayoutFile.SnapshotForUndo(); // 破坏性操作先留撤销点
        foreach (var w in Desktop.Windows)
        {
            if (!w.Attached) continue;
            LayoutFile.ClearMonitor(w.MonKey); // 各屏清各屏的（LayoutAll 重新右上列流分配）
            foreach (var iv in w._icons.Values) iv.Canon = null;
            w.LayoutAll(animated: true);
        }
    }

    private static void UndoArrange()
    {
        if (!LayoutFile.TryUndo()) return;
        Desktop.RefreshAll();               // 归属可能变了 → 重新分发
        Desktop.LayoutAllWindows(animated: true);
    }

    // ── 空白区：框选 / 背景菜单 ───────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        FocusDesktop();
        MenuHost.Dismiss(); // 兜底收掉可能残留的菜单
        CommitRename(); // 点空白 = 提交重命名（mac 行为）
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ClearSelection();

        EndBand(); // 上一个框选可能因捕获被打断而没收尾，先清掉（否则矩形泄漏在 Canvas 上）
        _bandActive = true;
        _bandOrigin = e.GetPosition(IconCanvas);
        _bandRect = new System.Windows.Shapes.Rectangle
        {
            Fill = Accent.BandFill,
            Stroke = Accent.BandStroke,
            StrokeThickness = 1,
            RadiusX = 2, RadiusY = 2,
        };
        System.Windows.Controls.Panel.SetZIndex(_bandRect, 1000);
        IconCanvas.Children.Add(_bandRect);
        Canvas.SetLeft(_bandRect, _bandOrigin.X);
        Canvas.SetTop(_bandRect, _bandOrigin.Y);
        RootGrid.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_bandActive || _bandRect == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 左键早已抬起（up 事件被别的窗口/菜单吃掉）→ 这不是框选，收尾别让框跟着鼠标走
            EndBand();
            return;
        }
        var p = e.GetPosition(IconCanvas);
        var rect = new Rect(_bandOrigin, p);
        Canvas.SetLeft(_bandRect, rect.X);
        Canvas.SetTop(_bandRect, rect.Y);
        _bandRect.Width = rect.Width;
        _bandRect.Height = rect.Height;
        PokeElement(_bandRect, 150); // 拖动期间持续续命（LayoutUpdated 只唤醒不续命）

        foreach (var iv in VisibleIcons)
        {
            var ivRect = new Rect(Canvas.GetLeft(iv.Root), Canvas.GetTop(iv.Root),
                                  iv.Root.ActualWidth, iv.Root.ActualHeight);
            bool hit = rect.IntersectsWith(ivRect);
            if (hit != _selection.Contains(iv)) SetSelected(iv, hit);
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 点击型交互壁纸（机主反馈驱动）：空白处原地点击（非框选拖拽）转发给收编壁纸的
        // 输入收件窗——Web（CEF）壁纸是 Chrome_RenderWidgetHostHWND 叶子（发宿主窗无效，
        // 真机实锤），场景型退回本窗。先补一发 MOVE 让 Chromium 的命中状态就位，再 DOWN/UP。
        // WE 原生路径靠自家鼠标钩子喂 click，我们的输入层挡在上面时钩子不喂，只能自己转发。
        if (_bandActive && _weWindow != IntPtr.Zero && Native.IsWindow(_weWindow))
        {
            var p = e.GetPosition(IconCanvas);
            if (Math.Abs(p.X - _bandOrigin.X) < 4 && Math.Abs(p.Y - _bandOrigin.Y) < 4)
            {
                IntPtr sink = WallpaperEngine.FindInputSink(_weWindow);
                var screen = RootGrid.PointToScreen(e.GetPosition(RootGrid)); // 物理 px
                var pt = new Native.POINT { X = (int)screen.X, Y = (int)screen.Y };
                Native.ScreenToClient(sink, ref pt);
                IntPtr lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
                Native.PostMessage(sink, 0x0200 /*WM_MOUSEMOVE*/, IntPtr.Zero, lp);
                Native.PostMessage(sink, 0x0201 /*WM_LBUTTONDOWN*/, (IntPtr)0x0001 /*MK_LBUTTON*/, lp);
                Native.PostMessage(sink, 0x0202 /*WM_LBUTTONUP*/, IntPtr.Zero, lp);
            }
        }
        EndBand();
    }

    /// <summary>结束/清理框选（无论正常松手还是捕获被打断）。可重入。</summary>
    private void EndBand()
    {
        if (!_bandActive && _bandRect == null) return;
        _bandActive = false;
        if (RootGrid.IsMouseCaptured) RootGrid.ReleaseMouseCapture();
        if (_bandRect != null) IconCanvas.Children.Remove(_bandRect);
        _bandRect = null;
    }

    private void OnCanvasRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EndBand(); // 残留框选态会劫持右键路由，先兜底清理
        var up = e.GetPosition(IconCanvas);
        // 按下时其实压着图标（按住轻微移动滑出到空白）→ 仍出那个图标的菜单
        if (PressNear(up) && _rightPress.Icon != null)
        {
            ShowIconMenu(_rightPress.Icon, MenuAnchor(up));
            return;
        }
        var pt = IconCanvas.PointToScreen(MenuAnchor(up)); // 物理像素屏幕坐标
        PrepareForMenu(); // 前台=桌面链（Progman）：自制菜单要，Explorer 的 TrackPopupMenu 也要稳定环境
        ClearSelection();
        // 原生菜单模式（开关开 + 未按 Alt）：把 WM_CONTEXTMENU 转发给父窗口 DefView，让 Explorer
        // 弹它自己的桌面菜单——用户系统上装的是 Win11 现代菜单还是经典菜单，就出哪个，我们不重建。
        // Alt+右键 = 强制走我们的自制菜单（含整理/叠放/更换壁纸/设置）。
        // hook 里的 WM_CONTEXTMENU 无条件吞噬保持不变：菜单的唯一来源要么是这里显式 Post，要么下面自制。
        if (Config.NativeBackgroundMenu && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            IntPtr lp = (IntPtr)(((int)pt.Y << 16) | ((int)pt.X & 0xFFFF));
            // 必须先判 DefView 句柄非空：PostMessage(NULL,…) 会投到本线程队列并返回 true，
            // 那样会早退成"右键无反应"。句柄没就绪或拒收都落回自制菜单。
            if (DesktopLayer.DefViewHwnd != IntPtr.Zero &&
                Native.PostMessage(DesktopLayer.DefViewHwnd, (uint)Native.WM_CONTEXTMENU, _hwnd, lp))
                return;
        }
        MenuHost.RequestBackgroundMenu((int)pt.X, (int)pt.Y, DesktopItemProvider.UserDesktop, _hwnd);
    }

    // ── 键盘：回车/F2 重命名、Delete 回收站、Esc ──────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_renameBox != null) return; // 重命名框自己处理按键
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.A when mods == ModifierKeys.Control:
                SelectAll();
                e.Handled = true;
                break;
            case Key.N when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                CreateNewFolder();
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control && _selection.Count > 0:
                ClipboardCopyCut(cut: false);
                e.Handled = true;
                break;
            case Key.X when mods == ModifierKeys.Control && _selection.Count > 0:
                ClipboardCopyCut(cut: true);
                e.Handled = true;
                break;
            case Key.V when mods == ModifierKeys.Control:
                PasteFiles();
                e.Handled = true;
                break;
            case Key.F5:
                Desktop.RefreshAll();
                e.Handled = true;
                break;
            // Ctrl +/-：按档位调整图标大小（与 Finder 一致；主键区与小键盘的 +/- 都接）。
            // 主键区 "+" 是 OemPlus 的 Shift 态（US/多数拉丁布局），所以容忍 Shift——否则
            // 用户按 Ctrl+Shift+= 得到的 Control|Shift 精确不等于 Control 会漏掉（浏览器 Ctrl++ 同款期待）。
            case Key.OemPlus or Key.Add when (mods & ~ModifierKeys.Shift) == ModifierKeys.Control:
                Desktop.StepIconSize(+1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract when (mods & ~ModifierKeys.Shift) == ModifierKeys.Control:
                Desktop.StepIconSize(-1);
                e.Handled = true;
                break;
            case Key.Enter or Key.F2 when _selection.Count == 1:
                StartRename(_selection.First());
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when _selection.Count > 0: // Del 或 mac 式 ⌘⌫（这里退格即可）
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                MoveSelection(e.Key);
                e.Handled = true;
                break;
            // 空格预览：有第三方预览器就切换预览；没装则不处理（Handled 不置，落回首字母定位）。
            // 只认无修饰键的裸空格（Ctrl/Shift/Alt+Space 留给系统/IME，不抢）。
            case Key.Space when mods == ModifierKeys.None && Config.SpacePreview && FocusPreviewPath is { } previewPath:
                if (FilePreview.Toggle(previewPath)) { _previewOpen = !_previewOpen; e.Handled = true; }
                break;
            case Key.Escape:
                RestoreCutIcons();
                ClearSelection();
                _previewOpen = false;
                e.Handled = true;
                break;
        }
    }

    // ── 剪贴板：复制/剪切/粘贴文件（走 shell CF_HDROP + Preferred DropEffect） ──

    private readonly HashSet<IconVisual> _cutIcons = new();

    private void ClipboardCopyCut(bool cut)
    {
        var items = _selection.Where(s => !s.Entry.Path.StartsWith("::")).ToList();
        if (items.Count == 0) return;
        var paths = new StringCollection();
        foreach (var s in items) paths.Add(s.Entry.Path);

        var data = new DataObject();
        data.SetFileDropList(paths);
        // "Preferred DropEffect"：告诉粘贴目标这是复制还是剪切（Explorer 约定）
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((uint)(cut ? 2 : 1))));
        try { Clipboard.SetDataObject(data, true); }
        catch { SystemSounds.Beep.Play(); return; }

        RestoreCutIcons();
        if (cut)
            foreach (var s in items) { _cutIcons.Add(s); s.Root.Opacity = 0.5; PokeElement(s.Root, 150); } // 剪切态半透明
    }

    private void RestoreCutIcons()
    {
        foreach (var iv in _cutIcons) { iv.Root.Opacity = 1; PokeElement(iv.Root, 150); }
        _cutIcons.Clear();
    }

    private void PasteFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList().Cast<string>().Where(f => !string.IsNullOrEmpty(f)).ToArray();
            if (files.Length == 0) return;
            bool cut = ClipboardWantsMove();

            var op = new Native.SHFILEOPSTRUCT
            {
                hwnd = _hwnd,
                wFunc = cut ? Native.FO_MOVE : Native.FO_COPY,
                pFrom = string.Join("\0", files) + "\0\0",
                pTo = DesktopItemProvider.UserDesktop + "\0\0",
                // 复制撞名自动改名（同文件夹粘贴出"- 副本"）；剪切不改名（撞名交系统处理）
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR
                                  | (cut ? 0 : Native.FOF_RENAMEONCOLLISION)),
            };
            Native.SHFileOperationW(ref op);
            if (cut) { try { Clipboard.Clear(); } catch { } RestoreCutIcons(); } // 剪切粘贴后清空（Explorer 行为）
            // 新图标交给 FileSystemWatcher
        }
        catch { SystemSounds.Beep.Play(); }
    }

    private static bool ClipboardWantsMove()
    {
        try
        {
            if (Clipboard.GetDataObject()?.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.Position = 0; ms.Read(b, 0, 4);
                return (BitConverter.ToUInt32(b, 0) & 2) != 0; // DROPEFFECT_MOVE
            }
        }
        catch { }
        return false;
    }

    private void SelectAll()
    {
        foreach (var iv in VisibleIcons) SetSelected(iv, true);
        _focusIcon ??= VisibleIcons.FirstOrDefault();
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_renameBox != null || _icons.Count == 0) return;
        var text = e.Text;
        if (string.IsNullOrEmpty(text) || char.IsControl(text[0])) return;

        var now = DateTime.Now;
        if ((now - _typeAheadAt).TotalMilliseconds > 900) _typeAhead = "";
        _typeAheadAt = now;
        _typeAhead += text;

        var match = VisibleIcons
            .OrderBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault(i => i.Entry.DisplayName.StartsWith(_typeAhead, StringComparison.CurrentCultureIgnoreCase))
            // 单字母重复输入时在同首字母项间轮转
            ?? VisibleIcons.FirstOrDefault(i => i.Entry.DisplayName.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        if (match != null) SelectOnly(match);
        e.Handled = true;
    }

    /// <summary>方向键在网格上移动选中焦点：沿方向最近、垂直偏移惩罚更重（保证同列/同行优先）。</summary>
    private void MoveSelection(Key dir)
    {
        var cur = _focusIcon ?? _selection.FirstOrDefault() ?? VisibleIcons.FirstOrDefault();
        if (cur == null) return;
        Point c = IconCenter(cur);
        IconVisual? best = null;
        double bestScore = double.MaxValue;
        foreach (var iv in VisibleIcons)
        {
            if (iv == cur) continue;
            Point p = IconCenter(iv);
            double dx = p.X - c.X, dy = p.Y - c.Y;
            bool ok = dir switch
            {
                Key.Left => dx < -1, Key.Right => dx > 1,
                Key.Up => dy < -1, Key.Down => dy > 1, _ => false
            };
            if (!ok) continue;
            bool horiz = dir is Key.Left or Key.Right;
            double along = horiz ? Math.Abs(dx) : Math.Abs(dy);
            double perp = horiz ? Math.Abs(dy) : Math.Abs(dx);
            double score = along + perp * 3; // 垂直偏移权重更高
            if (score < bestScore) { bestScore = score; best = iv; }
        }
        if (best != null)
        {
            SelectOnly(best);
            // 预览开着时让它跟随新焦点（QuickLook/Seer 的 macOS scrub 手感）
            if (_previewOpen && FocusPreviewPath is { } pv) FilePreview.Switch(pv);
        }
    }

    private Point IconCenter(IconVisual iv)
    {
        double l = Canvas.GetLeft(iv.Root), t = Canvas.GetTop(iv.Root);
        if (double.IsNaN(l)) l = 0;
        if (double.IsNaN(t)) t = 0;
        return new Point(l + CellW / 2, t + CellH / 2);
    }

    /// <summary>用所选项目新建文件夹（Finder 行为）：建夹坐在第一个选中项的位置、
    /// 选中项全部移入、进入重命名。多选右键菜单的自定义项触发。</summary>
    private void CreateFolderWithSelection()
    {
        var items = _selection.Select(s => s.Entry.Path).Where(p => !p.StartsWith("::")).ToArray();
        if (items.Length == 0) return;
        try
        {
            string baseDir = DesktopItemProvider.UserDesktop;
            string name = L.T("新建文件夹", "New Folder");
            string path = Path.Combine(baseDir, name);
            for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            {
                name = L.T($"新建文件夹 ({n})", $"New Folder ({n})");
                path = Path.Combine(baseDir, name);
            }
            // 项目搬走后文件夹坐第一个选中项的位置（Finder 行为）
            var first = _selection.FirstOrDefault(s => !s.Entry.Path.StartsWith("::"));
            if (first?.Canon != null)
            {
                LayoutFile.Set(MonKey, name, first.Canon);
                LayoutFile.Save();
            }
            Directory.CreateDirectory(path);
            MoveIntoFolder(items, path);
            Desktop.RefreshAll();
            if (_icons.TryGetValue(path, out var iv))
            {
                SelectOnly(iv);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StartRename(iv));
            }
        }
        catch { SystemSounds.Beep.Play(); }
    }

    private void CreateNewFolder()
    {
        try
        {
            string baseDir = DesktopItemProvider.UserDesktop;
            string name = L.T("新建文件夹", "New Folder");
            string path = Path.Combine(baseDir, name);
            for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            {
                name = L.T($"新建文件夹 ({n})", $"New Folder ({n})");
                path = Path.Combine(baseDir, name);
            }
            // 预归属到本屏第一个空格（否则新文件夹默认落到主屏）；自由摆放按脚印避让
            var occupied = OccupiedCellsForSeeding();
            var cell = NearestFreeCell((0, 0), occupied);
            LayoutFile.Set(MonKey, name, CellToCanon(cell.Item1, cell.Item2));
            LayoutFile.Save();

            Directory.CreateDirectory(path);
            Desktop.RefreshAll(); // 立即建图标，不等 FileSystemWatcher
            if (_icons.TryGetValue(path, out var iv))
            {
                SelectOnly(iv);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StartRename(iv));
            }
        }
        catch { SystemSounds.Beep.Play(); }
    }

    private void DeleteSelection()
    {
        var paths = _selection.Select(s => s.Entry.Path)
                              .Where(p => !p.StartsWith("::")).ToList();
        if (paths.Count == 0) return;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = _hwnd,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO, // 进回收站，确认与否随系统设置
        };
        Native.SHFileOperationW(ref op);
        // 图标移除交给 FileSystemWatcher
    }

    // ── inline 重命名（mac 式：回车触发、选中主文件名） ───────

    private void StartRename(IconVisual iv)
    {
        if (iv.Entry.Path.StartsWith("::")) return; // 回收站不可改
        CancelRename();

        string fileName = Path.GetFileName(iv.Entry.Path);
        string ext = Path.GetExtension(fileName);
        bool isLink = ext.ToLowerInvariant() is ".lnk" or ".url";
        // 快捷方式显示名不含扩展名，编辑的也是不含扩展名的部分
        string editText = isLink ? Path.GetFileNameWithoutExtension(fileName) : fileName;

        _renaming = iv;
        StartRenameCaretPump(); // 透传态：光标闪烁是纯渲染变化，定时催帧
        _renameBox = new TextBox
        {
            Text = editText,
            FontSize = 12 * S,
            FontFamily = LabelFontFamily,
            MinWidth = 60 * S,
            MaxWidth = CellW + 40 * S,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(2, 0, 2, 1),
        };
        TextOptions.SetTextFormattingMode(_renameBox, TextFormattingMode.Display);
        InputMethod.SetIsInputMethodEnabled(_renameBox, true); // 重命名框放开 IME，允许输入中文名
        iv.Root.CacheMode = null; // 缓存纹理里 TextBox 光标不闪、选区不刷新——编辑期摘缓存
        iv.LabelPlate.Child = _renameBox;
        _renameBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { CommitRename(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { CancelRename(); ke.Handled = true; }
        };
        // Tab 顺移重命名（Finder 行为）：提交当前，接着改下一个图标；Shift+Tab 反向。
        // Tab 是焦点导航键，必须在 Preview 阶段拦（KeyDown 已被 KeyboardNavigation 吃掉）
        _renameBox.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key != Key.Tab) return;
            var next = AdjacentIcon(iv, backward: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            CommitRename();
            if (next != null) { SelectOnly(next); StartRename(next); }
            ke.Handled = true;
        };
        _renameBox.LostKeyboardFocus += (_, _) => CommitRename();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _renameBox?.Focus();
            if (_renameBox == null) return;
            // mac 式：选中主文件名，不选扩展名
            int selLen = isLink ? _renameBox.Text.Length
                : Math.Max(0, _renameBox.Text.Length - Path.GetExtension(_renameBox.Text).Length);
            _renameBox.Select(0, selLen);
        });
    }

    /// <summary>视觉顺序（mac 右上列流：列从右往左、列内从上往下）里的相邻图标。</summary>
    private IconVisual? AdjacentIcon(IconVisual from, bool backward)
    {
        static double L(IconVisual i) { var v = Canvas.GetLeft(i.Root); return double.IsNaN(v) ? 0 : v; }
        static double T(IconVisual i) { var v = Canvas.GetTop(i.Root); return double.IsNaN(v) ? 0 : v; }
        var ordered = VisibleIcons
            .Where(i => !i.Entry.Path.StartsWith("::"))
            .OrderByDescending(i => Math.Round(L(i)))
            .ThenBy(T)
            .ToList();
        int idx = ordered.IndexOf(from);
        if (idx < 0 || ordered.Count < 2) return null;
        return ordered[(idx + (backward ? -1 : 1) + ordered.Count) % ordered.Count];
    }

    private void CommitRename()
    {
        var iv = _renaming;
        var box = _renameBox;
        if (iv == null || box == null) return;
        _renaming = null;
        _renameBox = null;
        StopRenameCaretPump();

        string oldPath = iv.Entry.Path;
        string oldName = Path.GetFileName(oldPath);
        string ext = Path.GetExtension(oldName);
        bool isLink = ext.ToLowerInvariant() is ".lnk" or ".url";

        string newText = box.Text.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) newText = newText.Replace(c.ToString(), "");
        string newName = isLink ? newText + ext : newText;

        RestoreLabel(iv);
        if (string.IsNullOrWhiteSpace(newText) || newName == oldName) return;

        try
        {
            string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
            if (File.Exists(newPath) || Directory.Exists(newPath)) { SystemSounds.Beep.Play(); return; }
            if (IsOnDesktop(oldPath))
            {
                // 先把布局位置过户给新名字（保持原归属显示器），watcher 重建图标时原位落座。
                // 堆叠子项（不在桌面根）必须跳过：布局档以文件名为键，Remove 会误伤同名桌面条目
                string owner = LayoutFile.FindOwner(oldName) ?? MonKey;
                if (iv.Canon != null) LayoutFile.Set(owner, newName, iv.Canon);
                LayoutFile.Remove(oldName);
                LayoutFile.Save();
            }
            if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
            else File.Move(oldPath, newPath);
            // 文件夹堆叠标记跟着改名走——必须在 Move 成功之后：失败时先迁了标，
            // 标记指向不存在的新路径，下一轮 RefreshAll 剪枝会把它当死路径静默删掉
            int fi = Config.StackFolders.FindIndex(f => PathEq(f, oldPath));
            if (fi >= 0) { Config.StackFolders[fi] = newPath; Config.Save(); }
        }
        catch { SystemSounds.Beep.Play(); }
    }

    private void CancelRename()
    {
        var iv = _renaming;
        _renaming = null;
        _renameBox = null;
        StopRenameCaretPump();
        if (iv != null) RestoreLabel(iv);
    }

    private void RestoreLabel(IconVisual iv)
    {
        iv.LabelPlate.Child = iv.Label;
        ApplyCacheMode(iv.Root); // 重命名摘掉的缓存挂回（见 ApplyCacheMode 注释）
    }

    // ── OLE 拖放（进 & 自我重定位） ───────────────────────────

    private static bool IsOnDesktop(string path)
    {
        var dir = Path.GetDirectoryName(path);
        return string.Equals(dir, DesktopItemProvider.UserDesktop, StringComparison.OrdinalIgnoreCase)
            || string.Equals(dir, DesktopItemProvider.PublicDesktop, StringComparison.OrdinalIgnoreCase);
    }

    private ShellDrag.IDropTargetHelper? _dropHelper;

    /// <summary>自家拖拽标记格式里的完整项列表（含虚拟项）；非自家拖拽返回 null。</summary>
    private static string[]? InternalDragPaths(System.Windows.IDataObject data)
    {
        try
        {
            if (data.GetData(ShellDrag.InternalFormat) is MemoryStream ms)
                return System.Text.Encoding.UTF8.GetString(ms.ToArray())
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { }
        return null;
    }

    private static bool IsInternalDrag(System.Windows.IDataObject data)
    {
        try { return data.GetDataPresent(ShellDrag.InternalFormat); } catch { return false; }
    }

    private void ComputeDragEffects(DragEventArgs e)
    {
        e.Handled = true;
        if (IsInternalDrag(e.Data)) { e.Effects = DragDropEffects.Move; return; } // 自家图标：重定位
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.All(IsOnDesktop))
            e.Effects = DragDropEffects.Move; // 资源管理器里的桌面文件夹拖来的也算重定位
        else
        {
            bool sameVolume = string.Equals(Path.GetPathRoot(paths[0]),
                Path.GetPathRoot(DesktopItemProvider.UserDesktop), StringComparison.OrdinalIgnoreCase);
            e.Effects = (sameVolume && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                ? DragDropEffects.Move : DragDropEffects.Copy; // Explorer 习惯：同卷移动、跨卷复制、Ctrl 强制复制
        }
    }

    // IDropTargetHelper 全程配合（Enter/Over/Leave/Drop），shell 拖拽图像才会在我们窗口上显示
    private void OnDesktopDragEnter(object sender, DragEventArgs e)
    {
        ComputeDragEffects(e);
        // 缓存本次拖拽的路径集（悬停命中要排除拖拽项自身，每帧解析数据对象太贵）
        _dragOverPaths = InternalDragPaths(e.Data)
            ?? (e.Data.GetDataPresent(DataFormats.FileDrop) ? (string[])e.Data.GetData(DataFormats.FileDrop)! : Array.Empty<string>());
        _dropHelper ??= ShellDrag.CreateDropTargetHelper();
        if (_dropHelper != null && ShellDrag.ComDataObject(e.Data) is { } com)
        {
            Native.GetCursorPos(out var pt);
            try { _dropHelper.DragEnter(_hwnd, com, ref pt, (uint)e.Effects); } catch { }
        }
    }

    private void OnDesktopDragOver(object sender, DragEventArgs e)
    {
        ComputeDragEffects(e);
        if (_dropHelper != null)
        {
            Native.GetCursorPos(out var pt);
            try { _dropHelper.DragOver(ref pt, (uint)e.Effects); } catch { }
        }
        UpdateSpringTarget(e.GetPosition(IconCanvas));
    }

    private void OnDesktopDragLeave(object sender, DragEventArgs e)
    {
        try { _dropHelper?.DragLeave(); } catch { }
        ClearSpring();
    }

    // ── 文件夹悬停高亮 + 弹簧打开（Finder 手感：实测规格 0.5s） ──
    // OLE 在悬停期间持续回调 DragOver（不动鼠标也来），时间戳判断即可，无需定时器。

    private string[] _dragOverPaths = Array.Empty<string>();
    private IconVisual? _springTarget;
    private DateTime _springStart;
    private bool _sprung;

    private void UpdateSpringTarget(Point pos)
    {
        var target = DropTargetIconAt(pos, _dragOverPaths);
        if (!ReferenceEquals(target, _springTarget))
        {
            HighlightDropTarget(_springTarget, false);
            _springTarget = target;
            _springStart = DateTime.UtcNow;
            _sprung = false;
            HighlightDropTarget(target, true); // 回收站也高亮（它是合法落点），弹簧只对真文件夹
        }
        else if (target != null && !_sprung
                 && (DateTime.UtcNow - _springStart).TotalMilliseconds >= 500
                 && Directory.Exists(target.Entry.Path))
        {
            _sprung = true; // 同一目标只弹一次，移开重悬停才再弹
            if (IsStackFolderIcon(target))
            {
                // 堆叠文件夹的弹簧 = 原地展开（Dock 语义），不再另开资源管理器；
                // 展开后子项虽不可作落点，文件夹本体仍在原位可收货
                if (_expandedStack != DirKey(target.Entry.Path) && _folderStackPending == null)
                {
                    BeginExpandFolderStack(target.Entry.Path);
                    Log.Write($"spring-expand: {target.Entry.DisplayName}");
                }
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target.Entry.Path}\"") { UseShellExecute = false });
                Log.Write($"spring-open: {target.Entry.DisplayName}");
            }
            catch (Exception ex) { Log.Write("spring open failed: " + ex.Message); }
        }
    }

    private void HighlightDropTarget(IconVisual? iv, bool on)
    {
        if (iv == null) return;
        bool lit = on || _selection.Contains(iv); // 取消高亮时恢复真实选中态视觉
        iv.IconPlate.Background = lit ? SelIconBg : Brushes.Transparent;
        iv.LabelPlate.Background = lit ? SelLabelBg : Brushes.Transparent;
        PokeElement(iv.Root, 150);
    }

    /// <summary>强调色变化后刷新当前选中态视觉（新选中自然用新色，这里补已选中的）。</summary>
    internal void RefreshAccent()
    {
        foreach (var iv in _selection) iv.LabelPlate.Background = SelLabelBg;
        if (_bandRect != null) { _bandRect.Fill = Accent.BandFill; _bandRect.Stroke = Accent.BandStroke; }
        PokeFrames(150);
    }

    private void ClearSpring()
    {
        HighlightDropTarget(_springTarget, false);
        _springTarget = null;
        _sprung = false;
        _dragOverPaths = Array.Empty<string>();
    }

    private void OnDesktopDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearSpring();
        if (_dropHelper != null && ShellDrag.ComDataObject(e.Data) is { } com)
        {
            Native.GetCursorPos(out var pt);
            try { _dropHelper.Drop(com, ref pt, (uint)e.Effects); } catch { }
        }
        var dropPos = e.GetPosition(IconCanvas);

        // 自家拖拽（含回收站等虚拟项）：落在文件夹/回收站图标上走移入/删除，否则重定位
        if (InternalDragPaths(e.Data) is { } own)
        {
            // 组内祖先已覆盖的后代剔除：文件夹和它展开的子项混拖时，SHFileOperation 收到
            // 重叠路径会先移走爹、再对儿子报"找不到文件"
            var realOwn = PruneDescendantPaths(own.Where(p => !p.StartsWith("::")).ToArray());
            if (realOwn.Length > 0 && DropTargetIconAt(dropPos, own) is { } target)
            {
                if (target.Entry.Path == DesktopItemProvider.RecycleBin) { DeleteViaShell(realOwn); return; }
                // 堆叠子项拖回自己爹 = 无事发生（同目录移动没有意义，shell 还会弹错误框）
                var moving = realOwn.Where(p => !PathEq(Path.GetDirectoryName(p), target.Entry.Path)).ToArray();
                if (moving.Length > 0) MoveIntoFolder(moving, target.Entry.Path);
                return;
            }
            // 堆叠子项拖到空白 = 移出到用户桌面（定案语义，Dock 网格拖出同款）。必须在
            // RepositionAt 之前拦：子项不在 _icons，RepositionAt 会当成跨屏移交、按文件名
            // 写布局条目（同名桌面项被污染）。按路径判断，跨屏窗口的落点也走这里。
            // !IsOnDesktop 加固：settings.json 被手改塞进桌面根也不至于误判桌面项为子项
            var fromStack = realOwn.Where(p => !IsOnDesktop(p) && Path.GetDirectoryName(p) is { } d && IsStackFolder(d)).ToArray();
            if (fromStack.Length > 0)
            {
                // 落点还在展开区自己的足迹里（手抖几像素的微拖）= 取消，不算"拖出"
                if (IsOverFolderStackArea(dropPos)) return;
                MoveIntoFolder(fromStack, DesktopItemProvider.UserDesktop); // 图标增删交给两侧 watcher
                var rest = own.Where(p => !fromStack.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (rest.Length > 0) RepositionAt(rest, dropPos);
                return;
            }
            RepositionAt(own, dropPos);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (DropTargetIconAt(dropPos, paths) is { } t2)
        {
            // 外来/桌面文件落在文件夹图标上 = 移入；落在回收站图标上 = 删除（Finder/Explorer 语义）
            if (t2.Entry.Path == DesktopItemProvider.RecycleBin) DeleteViaShell(paths);
            else MoveIntoFolder(paths, t2.Entry.Path);
            return;
        }
        if (paths.All(IsOnDesktop)) { RepositionAt(paths, dropPos); return; }

        // 外来文件：SHFileOperation 移动/复制到用户桌面（系统级冲突对话框）
        bool copy = e.Effects.HasFlag(DragDropEffects.Copy) && !e.Effects.HasFlag(DragDropEffects.Move)
                 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = _hwnd,
            wFunc = copy ? Native.FO_COPY : Native.FO_MOVE,
            pFrom = string.Join("\0", paths) + "\0\0",
            pTo = DesktopItemProvider.UserDesktop + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR),
        };
        // 预登记落点：watcher 建图标时按拖放位置落座
        PreassignDropPositions(paths.Select(Path.GetFileName).ToList()!, dropPos);
        Native.SHFileOperationW(ref op);
    }

    /// <summary>落点处的可接收图标：文件夹或回收站（拖拽的项自身除外）。</summary>
    private IconVisual? DropTargetIconAt(Point pos, string[] draggedPaths)
    {
        foreach (var iv in VisibleIcons)
        {
            bool folderish = Directory.Exists(iv.Entry.Path) || iv.Entry.Path == DesktopItemProvider.RecycleBin;
            if (!folderish) continue;
            if (draggedPaths.Contains(iv.Entry.Path, StringComparer.OrdinalIgnoreCase)) continue;
            double l = Canvas.GetLeft(iv.Root), t = Canvas.GetTop(iv.Root);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double hgt = iv.Root.ActualHeight > 0 ? iv.Root.ActualHeight : CellH;
            if (pos.X >= l && pos.X < l + CellW && pos.Y >= t && pos.Y < t + hgt) return iv;
        }
        return null;
    }

    private void MoveIntoFolder(string[] paths, string folder)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = _hwnd,
            wFunc = Native.FO_MOVE,
            pFrom = string.Join("\0", paths) + "\0\0",
            pTo = folder + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO,
        };
        Native.SHFileOperationW(ref op);
        // 图标移除交给 FileSystemWatcher
    }

    private void DeleteViaShell(string[] paths)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = _hwnd,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO, // 进回收站
        };
        Native.SHFileOperationW(ref op);
    }

    /// <summary>
    /// 把一组桌面项重定位到落点。本窗口的图标直接落座；不在本窗口的（跨屏拖拽）
    /// 写归属到本屏后全局刷新，由分发逻辑移交图标。
    /// 自家拖拽（ActiveDrag 有值）：锚点落在"拖拽图像视觉所在位置"（落点减抓取偏移），
    /// 组成员保持拖起时的相对位置（Finder 语义）。**自由摆放**下不播放飞行动画（图像已把图标
    /// 带到位、落哪是哪，零漂移）；**网格模式**下落点需吸格，从落点滑行到目标格（Finder 手感）。
    /// </summary>
    private void RepositionAt(string[] paths, Point dropPos)
    {
        // 叠放模式 = 自动整理，不接受手动落位（macOS 同款），也绝不写规范布局
        if (Config.UseStacks) { LayoutAll(animated: true); return; }
        var ctx = ActiveDrag;
        Point anchorTL = ctx != null
            ? new Point(dropPos.X - ctx.GrabOffset.X, dropPos.Y - ctx.GrabOffset.Y)
            : new Point(dropPos.X - CellW / 2, dropPos.Y - CellH / 2); // 无会话（外部拖入自家文件）退回居中

        var occupied = _icons.Values
            .Where(o => o.Canon != null && !paths.Contains(o.Entry.Path, StringComparer.OrdinalIgnoreCase))
            .Select(o => ClampCell(CanonToCell(o.Canon!)))
            .ToHashSet();
        bool ownershipChanged = false;
        double cascade = 0;
        foreach (var p in paths)
        {
            var iv = _icons.Values.FirstOrDefault(i =>
                string.Equals(i.Entry.Path, p, StringComparison.OrdinalIgnoreCase));
            string name = Path.GetFileName(p);
            if (string.IsNullOrEmpty(name)) name = p; // 虚拟项兜底
            Point rel = ctx != null && ctx.RelOffsets.TryGetValue(p, out var r)
                ? r : new Point(cascade, cascade);
            if (ctx == null) cascade += 8;
            double dl = anchorTL.X + rel.X, dt = anchorTL.Y + rel.Y;
            CanonPos canon;
            double fl, ft;
            if (Config.FreePlacement)
            {
                fl = Math.Clamp(dl, 0, Math.Max(0, WorkSize.W - CellW));
                ft = Math.Clamp(dt, 0, Math.Max(0, WorkSize.H - CellH));
                canon = PosToCanon(fl, ft);
            }
            else
            {
                var (sl, st) = SnapToGrid(dl, dt);
                var cell = NearestFreeCell(PosToCell(sl, st), occupied);
                occupied.Add(cell);
                (fl, ft) = CellPos(cell.Item1, cell.Item2);
                canon = CellToCanon(cell.Item1, cell.Item2);
            }
            LayoutFile.Set(MonKey, name, canon);
            if (iv != null)
            {
                iv.Canon = canon;
                // 自由摆放瞬置（零漂移）；网格模式从落点滑行到吸附格（回归被误删的动画）
                MoveIcon(iv, fl, ft, animated: !Config.FreePlacement);
            }
            else
            {
                ownershipChanged = true; // 图标属于别的窗口：跨屏拖拽换归属
            }
        }
        LayoutFile.Save();
        if (ownershipChanged)
        {
            Log.Write($"[{MonKey}] cross-screen drop: ownership transferred");
            Desktop.RefreshAll(); // 源窗口移除、本窗口按新 Canon 落座
        }
    }

    private void PreassignDropPositions(List<string?> names, Point dropPos)
    {
        if (Config.UseStacks) return; // 叠放模式不写规范布局，新文件直接进堆
        var occupied = _icons.Values.Where(o => o.Canon != null)
            .Select(o => ClampCell(CanonToCell(o.Canon!)))
            .ToHashSet();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (Config.FreePlacement)
            {
                LayoutFile.Set(MonKey, name, PosToCanon(dropPos.X - CellW / 2, dropPos.Y - CellH / 2));
            }
            else
            {
                var (sl, st) = SnapToGrid(dropPos.X - CellW / 2, dropPos.Y - CellH / 2);
                var cell = NearestFreeCell(PosToCell(sl, st), occupied);
                occupied.Add(cell);
                LayoutFile.Set(MonKey, name, CellToCanon(cell.Item1, cell.Item2));
            }
        }
        LayoutFile.Save();
    }

    // ── 壁纸镜像 ──────────────────────────────────────────────
    // 真透明不可行（WPF 分层子窗口不渲染，早期实测硬约束）→ 把系统当前壁纸按显示器
    // 画成本层背景并跟随变化（Desktop.Init 监听 UserPreferenceChanged 调回来）。
    // 限制：动态壁纸（Wallpaper Engine 等）只镜像不了，静态图/纯色/幻灯片当前帧都可以。

    private Image? _wallpaper;
    private string _wallpaperSig = "";

    internal void ApplyDesktopBackground()
    {
        if (_presenter != null) return; // 透传态：底就是真桌面，镜像/底色都不许画
        try
        {
            var info = Interop.DesktopWallpaper.ForMonitor(Monitor.Physical);

            string? path = info?.ImagePath
                ?? Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;

            // 签名比对：轮询兜底（设置应用走 IDesktopWallpaper 不广播 WM_SETTINGCHANGE，
            // 机主实测事件路径漏跟）每 8s 调进来，无变化即零开销早退，变了才重渲染
            string sig = $"{path}|{info?.Position}|{info?.BackColor}";
            if (sig == _wallpaperSig) return;
            _wallpaperSig = sig;

            // 底色兼命中测试面（Fit/Center 的留边也用它）
            RootGrid.Background = new SolidColorBrush(info?.BackColor ?? RegistryBackColor());
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                RemoveWallpaperImage();
                Log.Write($"[{MonKey}] wallpaper: solid color");
                return;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 读完即关文件，壁纸文件不被锁
            bmp.EndInit();
            bmp.Freeze();

            int pos = info?.Position ?? Interop.DesktopWallpaper.PosFill;
            if (pos == Interop.DesktopWallpaper.PosTile)
            {
                RemoveWallpaperImage();
                RootGrid.Background = new ImageBrush(bmp)
                {
                    TileMode = TileMode.Tile,
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    Viewport = new Rect(0, 0, bmp.Width, bmp.Height),
                    ViewportUnits = BrushMappingMode.Absolute,
                };
                Log.Write($"[{MonKey}] wallpaper: {Path.GetFileName(path)} (tile)");
                return;
            }

            BitmapSource src = bmp;
            var stretch = Stretch.UniformToFill; // FILL（默认）
            switch (pos)
            {
                case Interop.DesktopWallpaper.PosCenter: stretch = Stretch.None; break;
                case Interop.DesktopWallpaper.PosStretch: stretch = Stretch.Fill; break;
                case Interop.DesktopWallpaper.PosFit: stretch = Stretch.Uniform; break;
                case Interop.DesktopWallpaper.PosSpan: src = SpanCrop(bmp); break; // 裁本屏那块再铺满
            }

            if (_wallpaper == null)
            {
                _wallpaper = new Image { IsHitTestVisible = false, SnapsToDevicePixels = true };
                RenderOptions.SetBitmapScalingMode(_wallpaper, BitmapScalingMode.HighQuality);
                RootGrid.Children.Insert(0, _wallpaper); // IconCanvas 之下
            }
            _wallpaper.Source = src;
            _wallpaper.Stretch = stretch;
            Log.Write($"[{MonKey}] wallpaper: {Path.GetFileName(path)} pos={pos}");
        }
        catch (Exception ex) { Log.Write("wallpaper apply failed: " + ex.Message); }
    }

    private void RemoveWallpaperImage()
    {
        if (_wallpaper == null) return;
        RootGrid.Children.Remove(_wallpaper);
        _wallpaper = null;
    }

    private static Color RegistryBackColor()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Colors", "Background", null) is string rgb)
            {
                var p = rgb.Split(' ');
                if (p.Length == 3) return Color.FromRgb(byte.Parse(p[0]), byte.Parse(p[1]), byte.Parse(p[2]));
            }
        }
        catch { }
        return Colors.Black;
    }

    /// <summary>Span 模式：系统把一张图 cover 整个虚拟桌面，本窗口取自己显示器那一块。</summary>
    private BitmapSource SpanCrop(BitmapSource img)
    {
        double vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);   // SM_X/YVIRTUALSCREEN
        double vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);   // SM_CX/CYVIRTUALSCREEN
        if (vw < 1 || vh < 1) return img;
        double scale = Math.Max(vw / img.PixelWidth, vh / img.PixelHeight);
        double offX = (img.PixelWidth * scale - vw) / 2, offY = (img.PixelHeight * scale - vh) / 2;
        int x = (int)Math.Max(0, (Monitor.Physical.Left - vx + offX) / scale);
        int y = (int)Math.Max(0, (Monitor.Physical.Top - vy + offY) / scale);
        int w = (int)Math.Min(img.PixelWidth - x, Monitor.Physical.Width / scale);
        int h = (int)Math.Min(img.PixelHeight - y, Monitor.Physical.Height / scale);
        if (w < 1 || h < 1) return img;
        var crop = new CroppedBitmap(img, new Int32Rect(x, y, w, h));
        crop.Freeze();
        return crop;
    }

    // ── 动态壁纸（Wallpaper Engine 收编，"三明治 v3"）──────────
    // 真透明透传已证伪（WPF child 无法 layered，见 dev-notes"下午 VI"）；本模式不需要
    // WPF 窗隐身：把 WE 的每屏渲染窗从 WorkerW 收编进 DefView，z 序三层——
    //   UlwPresenter（图标帧，输入穿透）→ WE 渲染窗（壁纸，输入穿透）→ WPF 窗（收全部输入）。
    // WPF 窗被 WE 窗视觉盖住（≠隐身，绕开 layered 死穴），它的画面由 presenter 以
    // RootGrid RenderTargetBitmap → premult BGRA → ULW 呈现。脏驱动：静止零推送，
    // LayoutUpdated 催帧覆盖布局型变化，渲染专用变化各埋点 Poke，2s 心跳兜底 + WE 健康检查。

    /// <summary>交接退场标志：老进程 Close 时不把 WE 窗还原回 WorkerW（留在 DefView，
    /// 替身实例直接再收编，避免空窗期）。</summary>
    internal static bool HandoffRetiring;

    private UlwPresenter? _presenter;
    private IntPtr _weWindow;              // 收编的壁纸渲染窗（Zero = 非动态模式）
    private IntPtr _weOriginalParent;
    private int _weOriginalEx;
    private RenderTargetBitmap? _frameBitmap;
    private bool _pumpOn;
    private DateTime _pumpUntil;
    private DateTime _lastFramePush = DateTime.MinValue;
    private double _frameCostMs = 5; // EMA；自适应节流，渲染贵时自动降帧率
    private double _dpiK = 1.0;      // 本显示器物理 DPI 倍率（CoverAndSync 现查）
    private int _burstFrames;        // 帧泵诊断：本轮 burst 推了几帧
    private double _burstCostMs;
    private DispatcherTimer? _frameHeartbeat;
    private DispatcherTimer? _renameCaretPump;

    /// <summary>动态模式的帧成本控制：RTB 走软件光栅化，DropShadowEffect 的软件高斯模糊
    /// 是帧成本的绝对大头（4K 真机实测 avg 1031ms/帧 = 完全不可用，机主反馈"帧率很低"）。
    /// BitmapCache 在软件 RTB 路径不被复用（每帧重烙反而更贵，A/B 实测负优化）→ 动态模式
    /// 直接摘掉子树里所有 Effect（观感：图标少一层淡阴影，动态壁纸上不明显），退出时恢复。
    /// 镜像模式走原生 GPU 渲染，Effect 免费，全部保留——但 Root 要挂 BitmapCache（见
    /// ApplyCacheMode 注释）：GPU 路径的 Opacity&lt;1 中间层合成会把窗口表面 alpha 打洞，
    /// 透出身后的 WE 壁纸（堆叠展开/收起渐隐的"浅蓝背板"bug 根因）。</summary>
    private readonly Dictionary<FrameworkElement, Effect> _strippedEffects = new();

    private void ApplyCacheMode(FrameworkElement el)
    {
        if (_presenter != null && Config.DynamicNoShadows) StripEffects(el);
        else RestoreEffects(el);
        // WPF 硬件合成 bug（真机实锤，2026-07-07 深夜）：元素 Opacity∈(0,1) 时走"中间层"
        // 合成 pass，会把窗口表面该矩形的 alpha 打出洞——非分层子窗口的表面 alpha 照样参与
        // DWM 合成，洞里透出我们身后的桌面。WE 在后面跑时 = 透出亮色动态壁纸 = 机主报的
        // "浅蓝色方形背板"（堆叠展开/收起渐隐最明显）；WE 不跑时透出同一张静态壁纸，肉眼
        // 不可见，所以一直没被发现。修 = 给整个图标/堆 Root 挂 BitmapCache：带缓存元素的
        // 透明度合成走"纹理四边形"路径，不产生打洞的中间层；位移动画每帧只平移纹理还更省。
        // 动态模式摘缓存（软件 RTB 不复用 BitmapCache，每帧重烙是实测负优化，且 RTB 无此
        // bug）；重命名期间也要摘（TextBox 光标在缓存纹理里不刷新），见 StartRename。
        el.CacheMode = _presenter == null ? new BitmapCache { RenderAtScale = _dpiK } : null;
    }

    /// <summary>动态壁纸 + 机主勾了"禁用动画" = 布局动画全部瞬移（低配帧率保底）。</summary>
    private static bool AnimationsSuppressed(FrameworkElement el) =>
        Config.DynamicNoAnimations && Window.GetWindow(el) is MainWindow { _presenter: not null };

    /// <summary>设置里切换性能项（禁用阴影）后 live 重应用。</summary>
    internal void RefreshDynamicPerf()
    {
        if (_presenter == null) return;
        ApplyCacheModeAll();
        RenderFrame();
        PokeFrames(300);
    }

    private void StripEffects(DependencyObject node)
    {
        if (node is FrameworkElement fe && fe.Effect != null)
        {
            _strippedEffects[fe] = fe.Effect;
            fe.Effect = null;
        }
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) StripEffects(VisualTreeHelper.GetChild(node, i));
    }

    private void RestoreEffects(DependencyObject node)
    {
        if (node is FrameworkElement fe && _strippedEffects.TryGetValue(fe, out var eff))
        {
            fe.Effect = eff;
            _strippedEffects.Remove(fe);
        }
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) RestoreEffects(VisualTreeHelper.GetChild(node, i));
    }

    private void ApplyCacheModeAll()
    {
        foreach (var iv in _icons.Values) ApplyCacheMode(iv.Root);
        foreach (var pv in _stackPiles.Values) ApplyCacheMode(pv.Root);
        if (_folderStack is { } fs)
        {
            foreach (var c in fs.Children) ApplyCacheMode(c.Root);
            if (fs.Tile != null) ApplyCacheMode(fs.Tile);
        }
        if (_presenter == null) _strippedEffects.Clear(); // 已全数恢复，别攥着死引用
    }

    /// <summary>按"设置开关 + WE 是否在本屏渲染"进入/退出动态壁纸模式。
    /// 调用点：挂载后（CoverAndSync）、设置切换、Desktop 8s 轮询（WE 启动检测）、心跳（WE 健康）。</summary>
    internal void ApplyWallpaperMode()
    {
        if (!_attached || !_forceRectValid) return;
        IntPtr we = Config.DynamicWallpaper ? WallpaperEngine.FindForMonitor(Monitor.Physical) : IntPtr.Zero;
        if (we != IntPtr.Zero) EnterDynamic(we);
        else ExitDynamic(release: true);
    }

    private void EnterDynamic(IntPtr we)
    {
        if (_weWindow == we && _presenter != null)
        {
            AssertDynamicZOrder(); // 已在动态模式：只复核 z 序与矩形
            return;
        }
        if (_weWindow != IntPtr.Zero && _weWindow != we)
        {
            // WE 换壁纸重建了渲染窗：释放旧的（多半已死，Release 内部有 IsWindow 保护）
            WallpaperEngine.Release(_weWindow, _weOriginalParent, _weOriginalEx);
            _weWindow = IntPtr.Zero;
        }

        bool fresh = _presenter == null;
        if (fresh)
        {
            var p = UlwPresenter.Create(DesktopLayer.ParentHwnd, Native.HWND_TOP, _forceRect);
            if (p == null)
            {
                Log.Write($"[{MonKey}] dynamic wallpaper presenter FAILED; staying on mirror");
                return;
            }
            _presenter = p;
        }

        (_weOriginalParent, _weOriginalEx) = WallpaperEngine.Adopt(we, _forceRect);
        _weWindow = we;
        AssertDynamicZOrder();

        if (fresh)
        {
            // 停镜像：presenter 帧空白区 alpha=0，透出中层的 WE 窗
            RemoveWallpaperImage();
            _wallpaperSig = "";
            RootGrid.Background = Brushes.Transparent;

            if (DesktopLayer.NativeIconsVisible)
            {
                DesktopLayer.SetNativeIconsVisible(false); // 原生图标会透出来变双份
                Log.Write($"[{MonKey}] hid native icons for dynamic wallpaper");
            }

            RootGrid.LayoutUpdated += OnLayoutUpdatedPoke;
            int beat = 0;
            _frameHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _frameHeartbeat.Tick += (_, _) =>
            {
                if (_weWindow != IntPtr.Zero && !Native.IsWindow(_weWindow))
                {
                    // WE 退出/重建了渲染窗 → 重新探测（找到新窗就换乘，找不到回镜像）
                    _weWindow = IntPtr.Zero;
                    ApplyWallpaperMode();
                    return;
                }
                AssertDynamicZOrder(); // 廉价 Win32 调用，每拍都做
                // 兜底帧（漏埋点的渲染型变化）：全帧 RTB 在 4K 上一帧 ~0.2s CPU（软件光栅化
                // 全树 DropShadow，真机实测），交互路径有埋点+LayoutUpdated 唤醒兜着——
                // 只在长静止（30s 无任何推帧）时补一帧，静止 CPU 压到 ~0%
                if (++beat % 15 == 0 && (DateTime.UtcNow - _lastFramePush).TotalSeconds > 15)
                {
                    _lastFramePush = DateTime.UtcNow;
                    RenderFrame();
                }
            };
            _frameHeartbeat.Start();
        }

        ApplyCacheModeAll();
        RenderFrame();
        PokeFrames(600);
        Log.Write($"[{MonKey}] dynamic wallpaper ON we=0x{we:X} ({WallpaperEngine.Describe(we)}) presenter=0x{_presenter!.Hwnd:X} rect=({_forceRect.Left},{_forceRect.Top},{_forceRect.Width}x{_forceRect.Height})");
    }

    /// <summary>三层 z 序（presenter 顶、WE 中、WPF 底）+ 矩形复核。收编别家窗口，
    /// 谁都可能动它，心跳廉价重申一次。</summary>
    private void AssertDynamicZOrder()
    {
        if (_presenter == null || _weWindow == IntPtr.Zero) return;
        _presenter.Sync(Native.HWND_TOP, _forceRect);
        Native.SetWindowPos(_weWindow, _presenter.Hwnd, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(_hwnd, _weWindow, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void ExitDynamic(bool release)
    {
        if (_presenter == null && _weWindow == IntPtr.Zero) return;
        RootGrid.LayoutUpdated -= OnLayoutUpdatedPoke;
        _frameHeartbeat?.Stop();
        _frameHeartbeat = null;
        StopRenameCaretPump();
        if (_pumpOn) { CompositionTarget.Rendering -= OnPumpFrame; _pumpOn = false; }
        _presenter?.Dispose();
        _presenter = null;
        _frameBitmap = null;
        _patchBitmap = null;
        _dirtyTracked.Clear();
        _fullDirtyUntil = DateTime.MinValue;

        if (_weWindow != IntPtr.Zero)
        {
            if (release) WallpaperEngine.Release(_weWindow, _weOriginalParent, _weOriginalEx);
            _weWindow = IntPtr.Zero;
        }

        _wallpaperSig = "";
        ApplyDesktopBackground(); // 回镜像路径（含 RootGrid 底色恢复）
        ApplyCacheModeAll();      // 恢复 Effect + 挂回阴影 BitmapCache（GPU 路径防渐隐背板）
        Log.Write($"[{MonKey}] dynamic wallpaper OFF (release={release})");
    }

    /// <summary>渲染一帧到呈现层。VisualBrush + Stretch.Fill 把 RootGrid（含 LayoutTransform
    /// DPI 补偿）直接映射到物理像素矩形，省掉所有 DIU/DPI 换算。
    /// dirtyPhys 非空 = 脏区裁剪路径（P0-B）：只光栅化脏矩形（成本 ∝ 面积），patch 写进
    /// DIB 子矩形，未变区域保留上一帧。全帧路径（null）用于进入动态/兜底帧/泵收尾定格。</summary>
    private void RenderFrame(Rect? dirtyPhys = null)
    {
        if (_presenter == null || !_attached) return;
        int pw = Monitor.Physical.Width, ph = Monitor.Physical.Height;
        if (pw < 1 || ph < 1 || RootGrid.ActualWidth < 1) return;

        // 脏区太大时小 RTB 无优势（还多一次拷贝），退回全帧；_frameBitmap 为空或尺寸不符
        // 说明本尺寸还没推过全帧（patch 会叠在清零的新 DIB 上），必须全帧起步
        if (dirtyPhys is { } d0 && _frameBitmap?.PixelWidth == pw && _frameBitmap.PixelHeight == ph
            && d0.Width * d0.Height < pw * (double)ph * 0.5)
        {
            int x = Math.Max(0, (int)Math.Floor(d0.X)), y = Math.Max(0, (int)Math.Floor(d0.Y));
            int w = Math.Min(pw, (int)Math.Ceiling(d0.Right)) - x, h = Math.Min(ph, (int)Math.Ceiling(d0.Bottom)) - y;
            if (w < 1 || h < 1) return; // 脏区在屏外/空：本帧无事可做
            // 尺寸圆整到 32 的倍数提高 patch RTB 复用率（动画期间包围盒每帧微变）
            w = Math.Min(pw - x, (w + 31) & ~31);
            h = Math.Min(ph - y, (h + 31) & ~31);
            var t0p = DateTime.UtcNow;
            if (_patchBitmap == null || _patchBitmap.PixelWidth != w || _patchBitmap.PixelHeight != h)
                _patchBitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            else
                _patchBitmap.Clear();
            var dvp = new DrawingVisual();
            using (var dcp = dvp.RenderOpen())
            {
                dcp.PushTransform(new TranslateTransform(-x, -y));
                dcp.DrawRectangle(new VisualBrush(RootGrid), null, new Rect(0, 0, pw, ph));
                dcp.Pop();
            }
            _patchBitmap.Render(dvp);
            _presenter.PushDirty(_patchBitmap, x, y);
            double costP = (DateTime.UtcNow - t0p).TotalMilliseconds;
            _frameCostMs = costP; // 直赋不做 EMA：4K 真机实锤入场全帧 250ms 会把 EMA 拖住，
                                  // 动画期脏区帧便宜也拉不回 gap，整段动画只推 1-4 帧
            _burstFrames++;
            _burstCostMs += costP;
            return;
        }

        var t0 = DateTime.UtcNow;
        if (_frameBitmap == null || _frameBitmap.PixelWidth != pw || _frameBitmap.PixelHeight != ph)
            _frameBitmap = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        else
            _frameBitmap.Clear(); // RTB.Render 是叠加语义，透明底必须先清

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(RootGrid), null, new Rect(0, 0, pw, ph));
        _frameBitmap.Render(dv);
        _presenter.PushFrame(_frameBitmap);

        double cost = (DateTime.UtcNow - t0).TotalMilliseconds;
        _frameCostMs = cost; // 直赋（见 patch 分支注释）：贵的全帧后歇 2×cost 保护 UI 线程，
                             // 下一帧若是便宜的脏区帧，gap 立即回到高频
        _burstFrames++;
        _burstCostMs += cost;
    }

    // ── 脏区跟踪（P0-B）：谁在动/变，只重画谁的包围盒 ──────────────────
    // PokeElement 登记"这个元素接下来 ms 毫秒是脏源"；每个泵帧对所有在册元素取
    // 当前包围盒 ∪ 上一帧包围盒（飞行轨迹两端都要重画），映射到物理像素后交给
    // RenderFrame 裁剪光栅化。没有元素上下文的 PokeFrames（LayoutUpdated 唤醒、
    // 全局刷新）走全帧窗口 _fullDirtyUntil。

    private RenderTargetBitmap? _patchBitmap;
    private sealed class DirtyTrack { public DateTime Until; public Rect Last = Rect.Empty; }
    private readonly Dictionary<FrameworkElement, DirtyTrack> _dirtyTracked = new();
    private DateTime _fullDirtyUntil = DateTime.MinValue;

    private void PokeElement(FrameworkElement el, int ms = 350)
    {
        if (_presenter == null) return;
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        if (_dirtyTracked.TryGetValue(el, out var t)) { if (until > t.Until) t.Until = until; }
        else _dirtyTracked[el] = new DirtyTrack { Until = until };
        PokeFrames(ms, fullDirty: false);
    }

    /// <summary>本泵帧的脏区（物理像素），null = 全帧。元素包围盒按
    /// physX = elX(RootGrid DIU) × pw/ActualWidth 映射——RootGrid 的 LayoutTransform 在
    /// TransformToAncestor 里不出现、在 VisualBrush 内容尺寸里又被 Stretch.Fill 除掉，
    /// 两处相消，公式对任何 DPI 补偿系数都成立。</summary>
    private Rect? ComputeDirtyPhysical(DateTime now)
    {
        if (now < _fullDirtyUntil || _dirtyTracked.Count == 0) return null;
        int pw = Monitor.Physical.Width, ph = Monitor.Physical.Height;
        double sx = pw / RootGrid.ActualWidth, sy = ph / RootGrid.ActualHeight;
        Rect acc = Rect.Empty;
        List<FrameworkElement>? expired = null;
        foreach (var (el, t) in _dirtyTracked)
        {
            Rect cur = Rect.Empty;
            if (el.IsVisible && el.IsDescendantOf(RootGrid))
            {
                try
                {
                    var b = el.TransformToAncestor(RootGrid).TransformBounds(new Rect(el.RenderSize));
                    cur = new Rect(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
                }
                catch { return null; } // 变换链断了（元素刚被摘）：全帧兜底
            }
            if (cur != Rect.Empty) acc.Union(cur);
            if (t.Last != Rect.Empty) acc.Union(t.Last); // 上一帧位置也要重画（腾出的地方）
            t.Last = cur;
            if (now > t.Until) (expired ??= new List<FrameworkElement>()).Add(el);
        }
        if (expired != null) foreach (var el in expired) _dirtyTracked.Remove(el);
        if (acc == Rect.Empty) return Rect.Empty; // 在册元素全隐身且无遗留：无事可做
        acc.Inflate(12, 12); // 阴影模糊 + AA 余量（阴影保留模式 4K@225% 最大 ~10px）
        acc.Intersect(new Rect(0, 0, pw, ph));
        return acc;
    }

    /// <summary>催帧：把帧泵维持到 holdMs 后（动画/交互期间持续推帧，静止自动停）。
    /// fullDirty=true（默认，无元素上下文的调用点）= 窗口期内每帧全量渲染；
    /// 有元素上下文的走 PokeElement（脏区裁剪，P0-B）。</summary>
    internal void PokeFrames(int holdMs = 350, bool fullDirty = true)
    {
        if (_presenter == null) return;
        var until = DateTime.UtcNow.AddMilliseconds(holdMs);
        if (until > _pumpUntil) _pumpUntil = until;
        if (fullDirty && until > _fullDirtyUntil) _fullDirtyUntil = until;
        if (_pumpOn) return;
        _pumpOn = true;
        CompositionTarget.Rendering += OnPumpFrame;
    }

    /// <summary>LayoutUpdated 只当"从静止唤醒"的传感器：泵活跃期间它每帧都 fire（WPF 在
    /// Rendering 订阅期间每帧跑布局检查，与真实变化无关）——无脑 poke 会自激成永动机
    ///（真机实锤：静止 CPU 挂在 3%+）。泵停后 WPF 停渲染，此事件只在真变化时来。
    /// 动画的持续续命由 MoveElement/FadeTo 等显式埋点负责。</summary>
    private void OnLayoutUpdatedPoke(object? s, EventArgs e)
    {
        if (!_pumpOn) PokeFrames(250);
    }

    private void OnPumpFrame(object? s, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now > _pumpUntil)
        {
            CompositionTarget.Rendering -= OnPumpFrame;
            _pumpOn = false;
            RenderFrame(); // 收尾帧：动画终值定格
            if (_burstFrames >= 5) // 交互级 burst 就记（帧成本是当前调优焦点）
                Log.Write($"[{MonKey}] pump burst: {_burstFrames} frames avg {_burstCostMs / _burstFrames:F1}ms");
            _burstFrames = 0;
            _burstCostMs = 0;
            return;
        }
        // 自适应节流：渲染成本的 2 倍为间隔下限（帧贵自动降帧率，渲染占 UI 线程 ≤50%），
        // 最快 ~60fps。**不设上限**——上限会在帧成本高时变成"强制高频渲染"烤死 UI 线程
        //（4K avg 1031ms/帧 × 66ms 上限 = 交互全糊的元凶，真机实锤）。脏区帧便宜，
        // _frameCostMs 的 EMA 会自动落到 patch 成本，动画期逼近 60fps（P0-B 的目标）。
        double minGap = Math.Max(15, _frameCostMs * 2);
        if ((now - _lastFramePush).TotalMilliseconds < minGap) return;
        _lastFramePush = now;
        var dirty = ComputeDirtyPhysical(now);
        if (dirty == Rect.Empty) return; // 在册元素全隐身且无遗留：本帧跳过
        RenderFrame(dirty);
    }

    /// <summary>渲染型变化（不触发布局，LayoutUpdated 兜不住）的静态调用点用：
    /// 笔刷/透明度动画/换图所在窗口催帧 + 登记脏区跟踪（P0-B）。</summary>
    private static void PokeWindowOf(FrameworkElement el, int ms = 350) =>
        (Window.GetWindow(el) as MainWindow)?.PokeElement(el, ms);

    /// <summary>重命名期间光标闪烁是纯渲染变化，低频定时催帧让呈现层跟上。</summary>
    private void StartRenameCaretPump()
    {
        if (_presenter == null) return;
        if (_renameCaretPump == null)
        {
            _renameCaretPump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _renameCaretPump.Tick += RenameCaretTick;
        }
        _renameCaretPump.Start();
    }

    private void RenameCaretTick(object? s, EventArgs e) => PokeFrames(400);

    private void StopRenameCaretPump() => _renameCaretPump?.Stop();
}
