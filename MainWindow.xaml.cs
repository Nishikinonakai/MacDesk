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
    // mac 式网格（DIU）：112×112 方形格（对齐 Finder gridSpacing 实测值）
    private const double CellW = 96, CellH = 104, GapX = 16, GapY = 8; // pitch = 112 × 112

    private static readonly FontFamily LabelFontFamily = new("Segoe UI, Microsoft YaHei UI");
    private const double MarginTop = 14, MarginRight = 14, MarginBottom = 60, MarginLeft = 14;
    private const int IconPx = 256; // 取图尺寸（高分屏 64 DIU × 3x 也够），低分辨率源是白边锯齿的元凶

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
                    MessageBox.Show("挂载桌面层失败（找不到 Progman/DefView）。", "MacDesk");
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
            LayoutAll(animated: false);
        });
    }

    private void OnSystemDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _displayDebounce?.Stop(); _displayDebounce?.Start(); });

    protected override void OnClosed(EventArgs e)
    {
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

    private void OnDisplayChangedDebounced()
    {
        if (!_attached) return;
        // 重挂子窗口的活体改尺寸在 WPF/DPI 虚拟化下不可靠（多次实测：MoveWindow 后布局尺寸卡旧值）。
        // 而"启动时挂载"在任何分辨率都正确 → 退出让看门狗拉起全新实例接管（布局分档，零状态损失）。
        Log.Write("display change -> exit for watchdog relaunch");
        Services.Watchdog.EnsureRunning(App.LaunchModeArgs); // 保证有看门狗来接管重启
        Close();
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
            RootGrid.LayoutTransform = Math.Abs(k - 1) < 0.001 ? null : new ScaleTransform(k, k);

            // WPF 对重挂子窗口的 WM_SIZE 处理不可靠（布局尺寸会卡旧值）→ 显式按它自己的
            // 账本设 Width/Height。它随后发起的 SetWindowPos 会被 ForceCoverHook 钳回物理
            // 真值，内容比例由上面的 LayoutTransform 修正，三者对任何 DPI 错位组合都收敛。
            Width = Monitor.Physical.Width * ct.TransformFromDevice.M11;
            Height = Monitor.Physical.Height * ct.TransformFromDevice.M22;
            Log.Write($"[{MonKey}] covered; wpf scale={believed:F2} actual={actual:F2} correction={k:F3} wpf-size={Width:F0}x{Height:F0}");
        }
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
    internal void RefreshItems(IReadOnlyList<DesktopEntry> entries)
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
        if (added || _icons.Count > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LayoutAll(animated: true));
    }

    private IconVisual CreateIconVisual(DesktopEntry en)
    {
        var img = new Image
        {
            Width = 64, Height = 64,
            Source = IconLoader.Load(en.Path, IconPx),
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var iconPlate = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            Child = img,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        string labelText = TruncateLabel(en.DisplayName);
        var label = new TextBlock
        {
            Text = labelText,
            Foreground = Brushes.White,
            FontSize = 12,
            // mac 质感：中英文都上 Bold（机主反馈 SemiBold 英文仍偏细；Windows 自带，免费合法）
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, // 测量偏差时的兜底
            MaxHeight = 34,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.85 },
        };
        // 小字号必须走 Display 模式（对齐像素网格），配合 MoveIcon 的整数坐标吸附——
        // 亚像素落位是"有的标签清晰有的糊"的元凶
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        var labelPlate = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 2),
            Background = Brushes.Transparent,
            Child = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = CellW - 4,
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

        var iv = new IconVisual { Entry = en, Root = root, IconPlate = iconPlate, LabelPlate = labelPlate, Label = label };
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
            12, Brushes.White, null, TextFormattingMode.Display,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = CellW - 14, // labelPlate MaxWidth(CellW-4) − 左右 Padding(5+5)
            Trimming = TextTrimming.None,
        };
        return ft.Height <= 34.5; // TextBlock MaxHeight=34（两行）
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

    private const double PitchX = CellW + GapX, PitchY = CellH + GapY;

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

    /// <summary>网格格 → 规范锚距（纯 col/row 的函数，与分辨率无关；网格恒上锚）。</summary>
    private static CanonPos CellToCanon(int col, int row) =>
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
        return (w - MarginRight - CellW - col * (CellW + GapX), MarginTop + row * (CellH + GapY));
    }

    private int RowsPerColumn()
    {
        var (_, h) = WorkSize;
        return Math.Max(1, (int)((h - MarginTop - MarginBottom + GapY) / (CellH + GapY)));
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
        int row = (int)Math.Round((t - MarginTop) / (CellH + GapY));
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
        double rowExact = (t - MarginTop) / strideY;
        for (int col = (int)Math.Floor(colExact); col <= (int)Math.Ceiling(colExact); col++)
        {
            if (col < 0 || col > MaxCol()) continue;
            double cl = w - MarginRight - CellW - col * strideX;
            if (cl + CellW <= l || cl >= l + CellW) continue; // 该列与脚印无横向重叠
            for (int row = (int)Math.Floor(rowExact); row <= (int)Math.Ceiling(rowExact); row++)
            {
                if (row < 0 || row >= RowsPerColumn()) continue;
                double ct = MarginTop + row * strideY;
                if (ct + CellH <= t || ct >= t + CellH) continue;
                occupied.Add((col, row));
            }
        }
    }

    /// <summary>找空格用的占用集合：自由摆放按显示脚印（可跨格），网格按目标格。</summary>
    private HashSet<(int, int)> OccupiedCellsForSeeding()
    {
        var occ = new HashSet<(int, int)>();
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

        // 使用叠放（macOS Use Stacks）：独立的自动整理模式，不碰规范布局，关闭即恢复
        if (Config.UseStacks) { LayoutStacks(animated); return; }
        if (_stackPiles.Count > 0) ClearStacks();

        // 规范布局是唯一事实来源：重排前全员取有效 Canon（含归属离场显示器的孤儿——推导显示、不回写）
        foreach (var iv in _icons.Values)
        {
            iv.Canon = Desktop.EffectiveCanon(Path.GetFileName(iv.Entry.Path));
            // 自愈：拖拽/剪切之外不允许有半透明残留（曾有捕获被打断导致留影卡死的实例）
            if (iv.Root.Opacity < 1 && !_dragGhosts.Contains(iv) && !_cutIcons.Contains(iv))
                iv.Root.Opacity = 1;
        }

        var placed = new HashSet<(int, int)>();

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
        // 真实内容扇形（后/中/前三层真图标斜向叠放）：非"其他"分组用，front 恒显示，mid/back 按成员数显示
        public Image IconFront = null!, IconMid = null!, IconBack = null!;
        // 语义占位（泛用叠层卡片 + 居中展开箭头）："其他"分组用——混杂类型没有单一代表缩略图
        public Border GenericCard1 = null!, GenericCard2 = null!;
        public UIElement ChevronBadge = null!;
        public TextBlock Label = null!;
        public Border LabelPlate = null!;
        public string? FrontPath, MidPath, BackPath; // 已加载的真实图标路径缓存，跳过重复取图
        // hover 刮擦预览（macOS scrub）状态：横向划过堆时前层临时轮换成员图标，移出复位
        public List<IconVisual> Members = new();
        public bool IsGeneric;
        public int ScrubIndex = -1; // -1 = 未在刮擦，静息态
        public ImageSource? RestingFrontIcon;
    }

    private readonly Dictionary<string, PileVisual> _stackPiles = new();
    private string? _expandedStack;

    private const string OtherKind = "其他"; // 混杂兜底分组：没有连贯视觉身份，走语义占位而非真实缩略图

    private static readonly (string Kind, string[] Exts)[] StackKindTable =
    {
        ("应用程序", new[] { ".lnk", ".url", ".exe", ".appref-ms" }),
        ("图片", new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".ico", ".svg" }),
        ("文档", new[] { ".txt", ".md", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".csv" }),
        ("压缩包", new[] { ".zip", ".7z", ".rar", ".gz", ".tar", ".iso" }),
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

    private static readonly string[] DateBuckets = { "今天", "昨天", "本周", "本月", "更早" };
    private static readonly string[] SizeBuckets = { "小型", "中型", "大型", "超大型" };

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
        foreach (var iv in _icons.Values) iv.Root.Visibility = Visibility.Visible;
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

        static bool IsSingle(IconVisual i) => i.Entry.Path.StartsWith("::") || Directory.Exists(i.Entry.Path);
        var virt = _icons.Values.Where(i => i.Entry.Path.StartsWith("::"));
        var dirs = _icons.Values.Where(i => !i.Entry.Path.StartsWith("::") && IsSingle(i))
            .OrderBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        foreach (var iv in virt.Concat(dirs))
        {
            iv.Root.Visibility = Visibility.Visible;
            var (l, t) = Take();
            MoveIcon(iv, l, t, animated);
        }

        var (classify, order) = StackGrouping();
        var groups = _icons.Values.Where(i => !IsSingle(i))
            .GroupBy(i => classify(i.Entry))
            .ToDictionary(g => g.Key,
                g => g.OrderBy(i => i.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList());
        var kindOrder = order.Where(groups.ContainsKey).ToList();
        if (_expandedStack != null && !kindOrder.Contains(_expandedStack)) _expandedStack = null; // 展开中的堆消失（清空/改型）

        // 收起动画的"飞回堆位"批次：动画播完才 Collapsed。定时器触发时按"那一刻"的分组/展开
        // 状态重新核实——期间被再点开/换分组就跳过隐藏，不需要显式取消定时器，天然免疫重入。
        var justCollapsing = new List<IconVisual>();

        foreach (var kind in kindOrder)
        {
            var members = groups[kind];
            var pile = GetOrCreatePile(kind);
            UpdatePileVisual(pile, kind, members);
            pile.Root.ToolTip = $"{kind} · {members.Count} 项";
            bool expanded = _expandedStack == kind;
            pile.LabelPlate.Background = expanded ? Accent.LabelBrush : Brushes.Transparent;

            var (l, t) = Take();
            double pl = Math.Round(l), pt = Math.Round(t); // 堆槽位坐标 = 收起成员的停靠点
            Canvas.SetLeft(pile.Root, pl);
            Canvas.SetTop(pile.Root, pt);

            foreach (var m in members)
            {
                bool wasVisible = m.Root.Visibility == Visibility.Visible;
                if (expanded)
                {
                    // 展开：坐标此刻已停在堆位（见下），从堆位飞向目标格 = 天然"从堆里展开"
                    m.Root.Visibility = Visibility.Visible;
                    var (ml, mt) = Take();
                    MoveIcon(m, ml, mt, animated);
                }
                else if (wasVisible)
                {
                    // 刚收起（含首次开启叠放）：先飞回堆位，播完动画再 Collapsed——不要瞬间消失
                    MoveIcon(m, pl, pt, animated);
                    if (animated) justCollapsing.Add(m);
                    else m.Root.Visibility = Visibility.Collapsed; // 非动画通道（冷启动）：直接落位
                }
                else
                {
                    // 保持收起：坐标静默跟随堆位（堆增减会移动槽位），不触发可见动画
                    MoveIcon(m, pl, pt, false);
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
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // 归属用触发时刻的分组函数现算：350ms 窗口内换过分组依据的话，捕获的旧堆名会误判
                var (classify, _) = StackGrouping();
                foreach (var m in batch)
                    if (Config.UseStacks && _expandedStack != classify(m.Entry))
                        m.Root.Visibility = Visibility.Collapsed;
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
        var iconBack = MakePileLayer(46, 0.72, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0), 3, 0.3);
        var iconMid = MakePileLayer(53, 0.88, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 6, 5, 0), 3, 0.35);
        var iconFront = MakePileLayer(60, 1.0, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(0), 4, 0.4);

        // 语义占位："其他"这种混杂分组没有连贯的单一缩略图可代表，退回泛用叠层卡片 + 居中展开箭头
        // （对照 macOS：Other/N items 就是这种通用"一沓文档"图标，不是真实内容）。
        var card1 = new Border
        {
            Width = 54, Height = 54,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var card2 = new Border
        {
            Width = 57, Height = 57,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x58, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 5, 0),
        };
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 9,9 L 18,0"),
            Stroke = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var chevronBadge = new Border
        {
            Width = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            Child = chevron,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var plate = new Grid { Width = 76, Height = 74, HorizontalAlignment = HorizontalAlignment.Center };
        plate.Children.Add(card1);
        plate.Children.Add(card2);
        plate.Children.Add(iconBack);
        plate.Children.Add(iconMid);
        plate.Children.Add(iconFront);
        plate.Children.Add(chevronBadge);

        var label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontFamily = LabelFontFamily,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.85 },
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        var labelPlate = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 2),
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
            Root = root, IconFront = iconFront, IconMid = iconMid, IconBack = iconBack,
            GenericCard1 = card1, GenericCard2 = card2, ChevronBadge = chevronBadge,
            Label = label, LabelPlate = labelPlate,
        };
        _stackPiles[kind] = pv;

        // hover 刮擦预览（macOS scrub）：横向划过堆时前层轮换显示成员真实图标，不需要额外
        // 取图——每个成员桌面图标本就常驻加载了自己的 Image.Source，直接借用。
        root.MouseMove += (_, e) =>
        {
            if (pv.IsGeneric || pv.Members.Count < 2) return;
            double ratio = e.GetPosition(plate).X / plate.Width;
            int idx = Math.Clamp((int)(ratio * pv.Members.Count), 0, pv.Members.Count - 1);
            if (idx != pv.ScrubIndex)
            {
                pv.ScrubIndex = idx;
                pv.IconFront.Source = ((Image)pv.Members[idx].IconPlate.Child).Source;
            }
            e.Handled = true;
        };
        root.MouseLeave += (_, _) =>
        {
            if (pv.ScrubIndex == -1) return;
            pv.ScrubIndex = -1;
            pv.IconFront.Source = pv.RestingFrontIcon;
        };
        return pv;
    }

    /// <summary>按分组内容刷新堆的观感：非"其他"用最多 3 个真实成员图标斜向叠放，
    /// "其他"（混杂无连贯身份）用泛用叠层卡片 + 展开箭头。可见标签改两行"类别\nN 项"
    /// （对照 macOS "Other / 14 items"，不再只藏在 tooltip 里）。按日期/大小分组时没有
    /// "其他"式的混杂兜底概念（每一档都是真实文件），恒用真实图标扇形。</summary>
    private void UpdatePileVisual(PileVisual pile, string kind, List<IconVisual> members)
    {
        bool generic = Config.StackGroupBy == "kind" && kind == OtherKind;
        pile.GenericCard1.Visibility = generic ? Visibility.Visible : Visibility.Collapsed;
        pile.GenericCard2.Visibility = generic ? Visibility.Visible : Visibility.Collapsed;
        pile.ChevronBadge.Visibility = generic ? Visibility.Visible : Visibility.Collapsed;

        pile.IconFront.Visibility = generic ? Visibility.Collapsed : Visibility.Visible;
        pile.IconMid.Visibility = !generic && members.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        pile.IconBack.Visibility = !generic && members.Count >= 3 ? Visibility.Visible : Visibility.Collapsed;

        // 布局刷新可能打断进行中的刮擦：先把前层复位到静息图，否则下面 SetLayerIcon 的
        // 路径缓存命中会跳过重载，把刮擦帧误捕获成新的静息图（此后前层永久卡在错的成员上）
        if (pile.ScrubIndex != -1 && pile.RestingFrontIcon != null)
            pile.IconFront.Source = pile.RestingFrontIcon;

        if (!generic)
        {
            SetLayerIcon(pile.IconFront, ref pile.FrontPath, members[0].Entry.Path);
            if (members.Count >= 2) SetLayerIcon(pile.IconMid, ref pile.MidPath, members[1].Entry.Path);
            if (members.Count >= 3) SetLayerIcon(pile.IconBack, ref pile.BackPath, members[2].Entry.Path);
            pile.RestingFrontIcon = pile.IconFront.Source;
        }

        // hover 刮擦预览的状态跟着这次布局刷新：成员列表变了就复位（下次 MouseMove 会按新内容重算）
        pile.Members = members;
        pile.IsGeneric = generic;
        pile.ScrubIndex = -1;

        pile.Label.Text = $"{kind}\n{members.Count} 项";
    }

    /// <summary>只在成员路径变化时才重新取图——LayoutStacks 每次重排都会跑到这里，
    /// 裸取图是较贵的 shell COM 往返，缓存路径避免同一张图反复取（尤其三层扇形后 3 倍开销）。</summary>
    private static void SetLayerIcon(Image img, ref string? cachedPath, string path)
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

    private static void Advance(ref int col, ref int row, int rows)
    {
        row++;
        if (row >= rows) { row = 0; col++; }
    }

    private (int, int) PosToCell(double l, double t)
    {
        var (w, _) = WorkSize;
        int col = (int)Math.Round((w - MarginRight - CellW - l) / (CellW + GapX));
        int row = (int)Math.Round((t - MarginTop) / (CellH + GapY));
        return (col, row);
    }

    private static void MoveIcon(IconVisual iv, double l, double t, bool animated)
    {
        // 落点吸附整数 DIU：亚像素坐标会让整个图标（尤其文字）渲染发糊
        l = Math.Round(l);
        t = Math.Round(t);
        if (animated && !double.IsNaN(Canvas.GetLeft(iv.Root)))
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var ax = new DoubleAnimation(l, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };
            var ay = new DoubleAnimation(t, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };
            iv.Root.BeginAnimation(Canvas.LeftProperty, ax);
            iv.Root.BeginAnimation(Canvas.TopProperty, ay);
        }
        else
        {
            iv.Root.BeginAnimation(Canvas.LeftProperty, null);
            iv.Root.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(iv.Root, l);
            Canvas.SetTop(iv.Root, t);
        }
    }

    // ── 选择模型 ──────────────────────────────────────────────

    private static readonly SolidColorBrush SelIconBg = new(Color.FromArgb(0x52, 0x00, 0x00, 0x00));
    private static SolidColorBrush SelLabelBg => Accent.LabelBrush; // 强调色（设置里可换，默认 mac 选中蓝）

    private void SetSelected(IconVisual iv, bool on)
    {
        if (on) _selection.Add(iv); else _selection.Remove(iv);
        iv.IconPlate.Background = on ? SelIconBg : Brushes.Transparent;
        iv.LabelPlate.Background = on ? SelLabelBg : Brushes.Transparent;
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
        foreach (var iv in VisibleIcons)
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
            OpenEntry(iv.Entry);
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

        foreach (var g in group) { g.Root.Opacity = 0.35; _dragGhosts.Add(g); } // 原位留影
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
            foreach (var g in group) g.Root.Opacity = 1;
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
            foreach (var g in group) g.Root.Opacity = 1;
            _dragGhosts.Clear();
        }
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ax = new DoubleAnimation(cur.X, homeTL.X, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(cur.Y, homeTL.Y, dur) { EasingFunction = ease };
        ax.Completed += (_, _) => Land();
        ghost.BeginAnimation(Canvas.LeftProperty, ax);
        ghost.BeginAnimation(Canvas.TopProperty, ay);
    }

    /// <summary>拖拽图像：组图标按当前相对位置合成；包围盒过大退化为锚点图标+计数角标。物理像素。</summary>
    private (BitmapSource Image, Native.POINT Hotspot) RenderDragImage(List<IconVisual> group, Point cursor)
    {
        double dev = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double k = (RootGrid.LayoutTransform as ScaleTransform)?.ScaleX ?? 1.0;
        double scale = dev * k;

        static double L(IconVisual g) => double.IsNaN(Canvas.GetLeft(g.Root)) ? 0 : Canvas.GetLeft(g.Root);
        static double T(IconVisual g) => double.IsNaN(Canvas.GetTop(g.Root)) ? 0 : Canvas.GetTop(g.Root);
        static double H(IconVisual g) => g.Root.ActualHeight > 0 ? g.Root.ActualHeight : CellH;

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

        foreach (var iv in VisibleIcons)
        {
            var ivRect = new Rect(Canvas.GetLeft(iv.Root), Canvas.GetTop(iv.Root),
                                  iv.Root.ActualWidth, iv.Root.ActualHeight);
            bool hit = rect.IntersectsWith(ivRect);
            if (hit != _selection.Contains(iv)) SetSelected(iv, hit);
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e) => EndBand();

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
        PrepareForMenu();
        ClearSelection();
        var pt = IconCanvas.PointToScreen(MenuAnchor(up));
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
            case Key.Escape:
                RestoreCutIcons();
                ClearSelection();
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
            foreach (var s in items) { _cutIcons.Add(s); s.Root.Opacity = 0.5; } // 剪切态半透明
    }

    private void RestoreCutIcons()
    {
        foreach (var iv in _cutIcons) iv.Root.Opacity = 1;
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
        if (best != null) SelectOnly(best);
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
            string name = "新建文件夹";
            string path = Path.Combine(baseDir, name);
            for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            {
                name = $"新建文件夹 ({n})";
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
            string name = "新建文件夹";
            string path = Path.Combine(baseDir, name);
            for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            {
                name = $"新建文件夹 ({n})";
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
        _renameBox = new TextBox
        {
            Text = editText,
            FontSize = 12,
            FontFamily = LabelFontFamily,
            MinWidth = 60,
            MaxWidth = CellW + 40,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(2, 0, 2, 1),
        };
        TextOptions.SetTextFormattingMode(_renameBox, TextFormattingMode.Display);
        InputMethod.SetIsInputMethodEnabled(_renameBox, true); // 重命名框放开 IME，允许输入中文名
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
            // 先把布局位置过户给新名字（保持原归属显示器），watcher 重建图标时原位落座
            string owner = LayoutFile.FindOwner(oldName) ?? MonKey;
            if (iv.Canon != null) LayoutFile.Set(owner, newName, iv.Canon);
            LayoutFile.Remove(oldName);
            LayoutFile.Save();
            if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
            else File.Move(oldPath, newPath);
        }
        catch { SystemSounds.Beep.Play(); }
    }

    private void CancelRename()
    {
        var iv = _renaming;
        _renaming = null;
        _renameBox = null;
        if (iv != null) RestoreLabel(iv);
    }

    private void RestoreLabel(IconVisual iv) => iv.LabelPlate.Child = iv.Label;

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
    }

    /// <summary>强调色变化后刷新当前选中态视觉（新选中自然用新色，这里补已选中的）。</summary>
    internal void RefreshAccent()
    {
        foreach (var iv in _selection) iv.LabelPlate.Background = SelLabelBg;
        if (_bandRect != null) { _bandRect.Fill = Accent.BandFill; _bandRect.Stroke = Accent.BandStroke; }
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
            var realOwn = own.Where(p => !p.StartsWith("::")).ToArray();
            if (realOwn.Length > 0 && DropTargetIconAt(dropPos, own) is { } target)
            {
                if (target.Entry.Path == DesktopItemProvider.RecycleBin) DeleteViaShell(realOwn);
                else MoveIntoFolder(realOwn, target.Entry.Path);
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
    /// 组成员保持拖起时的相对位置（Finder 语义）；且不播放飞行动画——图像已经把图标带到位了。
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
                MoveIcon(iv, fl, ft, animated: false);
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
}
