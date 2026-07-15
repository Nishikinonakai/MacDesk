using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDesk.Services;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using DockPanel = System.Windows.Controls.DockPanel;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Image = System.Windows.Controls.Image;
using FontWeights = System.Windows.FontWeights;
using Control = System.Windows.Controls.Control;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace MacDesk;

/// <summary>
/// 设置窗口 v2：macOS 系统设置同款"左侧栏 + 右内容"布局（机主点名的设计）。
/// 通用 / 外观（强调色）/ 菜单（黑名单）/ 关于（Claude 出品 + GitHub 检查更新）。
/// 纯 WPF 代码构 UI，改动即存，共享 Desktop.Config。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void ShowSingleton()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private static Settings Config => Desktop.Config;

    // mac 系统设置观感：颜色随系统深色/浅色 app 模式切换（机主系统是深色 app 模式，
    // 设置窗之前恒是浅色 mac 风——这批改成读注册表跟随，每次开窗现读一次即可，
    // 不需要监听实时切换：系统主题切换时设置窗通常没开着）。
    private readonly bool _dark = DetectDarkMode();
    private readonly Brush SidebarBg, ContentBg, CardBg, CardBorder, Subtle, TextFg,
        ButtonBg, ButtonBorder, ButtonHoverBg, FieldBg, FieldBorder, AccentRingSelected, DangerFg;

    private static bool DetectDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // 值不存在按浅色算（该键 Win10 1809 前不存在，微软自己文档里的默认假设）
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_dark) return;
        try
        {
            int on = 1;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { } // 老版本 dwmapi 没有这个 attribute，标题条留浅色不影响功能
    }

    private readonly ContentControl _page = new();
    private readonly ListBox _nav = new();

    private SettingsWindow()
    {
        (SidebarBg, ContentBg, CardBg, CardBorder, Subtle, TextFg, ButtonBg, ButtonBorder,
            ButtonHoverBg, FieldBg, FieldBorder, AccentRingSelected, DangerFg) = _dark
            ? (Rgb(0x26, 0x26, 0x29), Rgb(0x1E, 0x1E, 0x20), Rgb(0x2C, 0x2C, 0x2F), Rgb(0x3A, 0x3A, 0x3E),
               Rgb(0x9A, 0x9A, 0x9F), Rgb(0xF2, 0xF2, 0xF3), Rgb(0x3A, 0x3A, 0x3E), Rgb(0x4A, 0x4A, 0x4E),
               Rgb(0x46, 0x46, 0x4B), Rgb(0x23, 0x23, 0x26), Rgb(0x3A, 0x3A, 0x3E), Rgb(0xE5, 0xE5, 0xE5), Rgb(0xFF, 0x6B, 0x6B))
            : (Rgb(0xEC, 0xEC, 0xEE), Rgb(0xF7, 0xF7, 0xF9), Brushes.White, Rgb(0xE3, 0xE3, 0xE6),
               Rgb(0x6E, 0x6E, 0x73), Brushes.Black, Rgb(0xFF, 0xFF, 0xFF), Rgb(0xD0, 0xD0, 0xD5),
               Rgb(0xE5, 0xE5, 0xE8), Brushes.White, Rgb(0xD0, 0xD0, 0xD5), Rgb(0x33, 0x33, 0x38), Rgb(0xC0, 0x2B, 0x2B));

        Title = L.T("MacDesk 设置", "MacDesk Settings");
        Width = 760;
        Height = 600; // 目标：每页一屏放完不滚动（黑名单编辑器所在页除外）；768 高小屏减任务栏仍放得下
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = ContentBg;
        Foreground = TextFg; // Foreground 是继承属性：子树里没单独设色的 TextBlock 全部自动跟随
        Resources.Add(typeof(Button), ButtonStyle());
        // 原生 ComboBox 的 chrome 不吃 Background（深色下真机实测仍是白块，像素采样 rgb(233,233,233)）
        // → 深色时最小重模板；浅色保持原生（已验证过的观感，别动）
        if (_dark) Resources.Add(typeof(ComboBox), ComboStyle());
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/macdesk.ico")); } catch { }

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ── 左侧栏 ──
        var side = new Border { Background = SidebarBg, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideStack = new StackPanel { Margin = new Thickness(10, 14, 10, 10) };
        sideStack.Children.Add(new TextBlock
        {
            Text = "MacDesk",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 10),
        });
        _nav.BorderThickness = new Thickness(0);
        _nav.Background = Brushes.Transparent;
        _nav.ItemContainerStyle = NavItemStyle();
        // Tag = 语言无关的稳定键（ShowPage 按键路由，显示文本另算）。
        // 导航按语义分类、页内按使用频率排；排障向开关全部沉到"高级"，别让普通用户翻到。
        foreach (var (icon, key, name) in new[]
        {
            ("⚙️", "general", L.T("通用", "General")),
            ("🖥️", "desktop", L.T("桌面", "Desktop")),
            ("🎨", "appearance", L.T("外观", "Appearance")),
            ("📋", "menu", L.T("右键菜单", "Context Menu")),
            ("🛠️", "advanced", L.T("高级", "Advanced")),
            ("ℹ️", "about", L.T("关于", "About")),
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = key });
        }
        _nav.SelectionChanged += (_, _) => ShowPage(((ListBoxItem)_nav.SelectedItem).Tag as string ?? "general");
        sideStack.Children.Add(_nav);
        side.Child = sideStack;
        Grid.SetColumn(side, 0);
        root.Children.Add(side);

        // ── 右内容 ──
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);

        Content = root;
        _nav.SelectedIndex = 0;

        // 重获焦点时若停在外观页就重建：让图标大小滑杆/强调色同步 Ctrl+/- 等外部改动
        // （只在外观页做——该页无文本输入框，重建无副作用；菜单页有黑名单输入框不能重建）。
        Activated += (_, _) => { if ((_nav.SelectedItem as ListBoxItem)?.Tag as string == "appearance") ShowPage("appearance"); };
    }

    private Style NavItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 7, 8, 7)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        // ListBoxItem 是 Control，自带 Foreground 默认值会掐断窗口级 Foreground 的继承链——
        // 深色下侧栏文本不跟白（机主反馈可读性差）就是这个断点，必须在 item 样式里显式续上
        style.Setters.Add(new Setter(Control.ForegroundProperty, TextFg));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(8, 7, 8, 7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        template.VisualTree = border;
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x8A)), "bd"));
        template.Triggers.Add(selected);
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    private void ShowPage(string key) => _page.Content = key switch
    {
        "desktop" => BuildDesktop(),
        "appearance" => BuildAppearance(),
        "menu" => BuildMenuPage(),
        "advanced" => BuildAdvanced(),
        "about" => BuildAbout(),
        _ => BuildGeneral(),
    };

    // ── 页面骨架 ──────────────────────────────────────────────

    private static StackPanel Page(string title)
    {
        var p = new StackPanel { Margin = new Thickness(24, 18, 24, 24) };
        p.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 14),
        });
        return p;
    }

    /// <summary>卡片上方的小节标题（macOS 系统设置的分组小灰字）：滚动/扫视时的路标。</summary>
    private TextBlock Section(string title) => new()
    {
        Text = title,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Subtle,
        Margin = new Thickness(4, 2, 0, 6),
    };

    private Border Card(UIElement content) => new()
    {
        Background = CardBg,
        BorderBrush = CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 6, 16, 6),
        Margin = new Thickness(0, 0, 0, 14),
        Child = content,
    };

    private UIElement Row(string label, UIElement control, string? hint = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (hint != null)
            left.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Subtle, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(left);
        grid.Children.Add(control);
        return grid;
    }

    private Border Separator() => new()
    {
        Height = 1,
        Background = CardBorder,
        Margin = new Thickness(-16, 0, -16, 0),
    };

    /// <summary>macOS 系统设置同款开关（替代原生 CheckBox——深色下不适配且没有 mac 灵魂）：
    /// 胶囊底 + 白色圆钮，开=强调色/关=灰，160ms 滑动 + 变色动画。</summary>
    private Border Toggle(bool initial, Action<bool> onChange)
    {
        const double W = 38, H = 22, Pad = 2;
        double thumbTravel = W - H; // 圆钮直径 = H - 2*Pad，行程 = 宽 - 高
        bool on = initial;

        var offBrush = _dark ? Rgb(0x5A, 0x5A, 0x5E) : Rgb(0xD1, 0xD1, 0xD6);
        static Color OnColor() // LabelBrush 带 0xE6 透明度，开关要不透明版强调色
        { var c = Accent.LabelBrush.Color; return Color.FromRgb(c.R, c.G, c.B); }

        var thumb = new Border
        {
            Width = H - Pad * 2, Height = H - Pad * 2,
            CornerRadius = new CornerRadius((H - Pad * 2) / 2),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Pad, 0, 0, 0),
            RenderTransform = new TranslateTransform(on ? thumbTravel : 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 3, ShadowDepth = 0.5, Opacity = 0.35 },
        };
        var pill = new Border
        {
            Width = W, Height = H,
            CornerRadius = new CornerRadius(H / 2),
            Background = new SolidColorBrush(on ? OnColor() : offBrush.Color),
            Child = thumb,
            Cursor = Cursors.Hand,
        };
        pill.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            var slide = new System.Windows.Media.Animation.DoubleAnimation(
                on ? thumbTravel : 0, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
            ((TranslateTransform)thumb.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slide);
            var tint = new System.Windows.Media.Animation.ColorAnimation(
                on ? OnColor() : offBrush.Color, TimeSpan.FromMilliseconds(160));
            ((SolidColorBrush)pill.Background).BeginAnimation(SolidColorBrush.ColorProperty, tint);
            onChange(on);
        };
        return pill;
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    /// <summary>图标大小滑杆（macOS Dock 设置观感）：轨道 + 白圆钮，**无极调节**（拖到任意大小），
    /// 仅默认档一个强调色刻度作磁吸点；松手才应用一次（拖动中重建会卡）。Ctrl +/- 另走离散档位。
    /// 自绘（本项目控件全代码构，避开 WPF Slider 模板 Track 的 FrameworkElementFactory 坑）。</summary>
    private UIElement IconSizeSlider()
    {
        const double W = 220, D = 18, R = D / 2, RailH = 4, MidY = 12;
        var accent = new SolidColorBrush(Accent.Current);

        // 滑杆值域 32..128（下探到 32：4K 高 DPI 下 64 傻大，小端要够得着——实测 40 舒适、32 紧凑）
        double CenterX(int value) => R + (Math.Clamp(value, 32, 128) - 32) / 96.0 * (W - D);
        const double SnapPx = 8; // 默认档磁吸半径（px）
        double defaultX = CenterX(Desktop.DefaultIconSize);
        // 无极：像素→连续整数尺寸；只在靠近默认档时吸附（滑杆上唯一吸附点；Ctrl +/- 另走档位阶梯）
        int ValueFromX(double x)
        {
            if (Math.Abs(x - defaultX) <= SnapPx) return Desktop.DefaultIconSize;
            double f = Math.Clamp((x - R) / (W - D), 0, 1);
            return (int)Math.Round(32 + f * 96);
        }

        var canvas = new Canvas { Width = W, Height = 24 };
        var rail = new Border { Width = W - 2, Height = RailH, CornerRadius = new CornerRadius(RailH / 2), Background = FieldBorder };
        Canvas.SetLeft(rail, 1); Canvas.SetTop(rail, MidY - RailH / 2);
        canvas.Children.Add(rail);
        var fill = new Border { Height = RailH, CornerRadius = new CornerRadius(RailH / 2), Background = accent };
        Canvas.SetLeft(fill, 1); Canvas.SetTop(fill, MidY - RailH / 2);
        canvas.Children.Add(fill);

        // 无极调节：只画默认档这一个强调色刻度（唯一吸附点）——不再画各档刻度以免误导成离散停靠
        var dftTick = new Border { Width = 2, Height = 12, Background = accent, CornerRadius = new CornerRadius(1) };
        Canvas.SetLeft(dftTick, CenterX(Desktop.DefaultIconSize) - 1);
        Canvas.SetTop(dftTick, MidY - 6);
        canvas.Children.Add(dftTick);

        var thumb = new Border
        {
            Width = D, Height = D, CornerRadius = new CornerRadius(R),
            Background = Brushes.White,
            BorderBrush = FieldBorder, BorderThickness = new Thickness(0.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0.5, Opacity = 0.35 },
            Cursor = Cursors.Hand,
        };
        Canvas.SetTop(thumb, MidY - R);
        canvas.Children.Add(thumb);

        void Place(int value)
        {
            double cx = CenterX(value);
            Canvas.SetLeft(thumb, cx - R);
            fill.Width = Math.Max(0, cx - 1);
        }
        Place(Config.IconSize);

        // 拖动时滑钮实时跟随（无极，靠近默认磁吸），松手才真正应用一次——SetIconSize 会 teardown+
        // 重建桌面（含 shell 重取图），拖动中每变一次都做会连做几十次卡顿；松手应用一次既跟手又不抖。
        int pending = Config.IconSize;
        void Preview(double x) { pending = ValueFromX(x); Place(pending); }
        canvas.MouseLeftButtonDown += (_, e) => { canvas.CaptureMouse(); Preview(e.GetPosition(canvas).X); e.Handled = true; };
        canvas.MouseMove += (_, e) =>
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && canvas.IsMouseCaptured)
                Preview(e.GetPosition(canvas).X);
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!canvas.IsMouseCaptured) return;
            canvas.ReleaseMouseCapture();
            Preview(e.GetPosition(canvas).X);
            Desktop.SetIconSize(pending); // 一次应用（相同值内部早退）
        };

        // 端点 + 默认标注
        var captions = new Canvas { Width = W, Height = 16, Margin = new Thickness(0, 2, 0, 0) };
        void Cap(string text, double centerX, Brush brush, FontWeight weight)
        {
            var tb = new TextBlock { Text = text, FontSize = 10, Foreground = brush, FontWeight = weight };
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, Math.Clamp(centerX - tb.DesiredSize.Width / 2, 0, W - tb.DesiredSize.Width));
            captions.Children.Add(tb);
        }
        Cap(L.T("小", "Small"), CenterX(32), Subtle, FontWeights.Normal);
        Cap(L.T("默认", "Default"), CenterX(Desktop.DefaultIconSize), accent, FontWeights.SemiBold);
        Cap(L.T("大", "Large"), CenterX(128), Subtle, FontWeights.Normal);

        var wrap = new StackPanel { Width = W };
        wrap.Children.Add(canvas);
        wrap.Children.Add(captions);
        return wrap;
    }

    /// <summary>mac 风扁平按钮：圆角 + 主题色背板，悬停微亮/微暗。整窗通过
    /// Resources[typeof(Button)] 隐式套用，7 处按钮调用点都不用改。</summary>
    private Style ButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ButtonBg));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TextFg));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ButtonBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var tp = System.Windows.Data.RelativeSource.TemplatedParent;
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = tp });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = tp });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = tp });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = tp });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ButtonHoverBg, "bd"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.5));
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    /// <summary>深色 ComboBox 最小重模板：主体 Border + 右侧箭头 + 下拉 Popup。
    /// 放弃原生 chrome 意味着键盘展开等细节从简——这个控件只在黑名单页做鼠标挑选，够用。</summary>
    private Style ComboStyle()
    {
        var tp = System.Windows.Data.RelativeSource.TemplatedParent;
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TextFg));

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextFg));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(ComboBox.ItemContainerStyleProperty, itemStyle));

        var template = new ControlTemplate(typeof(ComboBox));
        var root = new FrameworkElementFactory(typeof(Grid));

        // 整块都是开关：ToggleButton 铺满，模板换成主题 Border + 手画箭头
        var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = tp, Mode = System.Windows.Data.BindingMode.TwoWay });
        toggle.SetValue(FocusableProperty, false);
        var toggleTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton));
        var body = new FrameworkElementFactory(typeof(Border));
        body.SetValue(Border.BackgroundProperty, FieldBg);
        body.SetValue(Border.BorderBrushProperty, FieldBorder);
        body.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        body.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,0 L 4,4 L 8,0"));
        arrow.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Subtle);
        arrow.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        arrow.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(MarginProperty, new Thickness(0, 0, 9, 0));
        body.AppendChild(arrow);
        toggleTemplate.VisualTree = body;
        toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        root.AppendChild(toggle);

        // 选中项文本（不截点击，让它落到下面的 toggle）
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty,
            new System.Windows.Data.Binding("SelectionBoxItem") { RelativeSource = tp });
        content.SetValue(MarginProperty, new Thickness(9, 3, 26, 3));
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(IsHitTestVisibleProperty, false);
        root.AppendChild(content);

        var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup));
        popup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty,
            new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = tp });
        popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty,
            System.Windows.Controls.Primitives.PlacementMode.Bottom);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.StaysOpenProperty, false);
        var drop = new FrameworkElementFactory(typeof(Border));
        drop.SetValue(Border.BackgroundProperty, CardBg);
        drop.SetValue(Border.BorderBrushProperty, FieldBorder);
        drop.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        drop.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        drop.SetValue(MaxHeightProperty, 260.0);
        drop.SetValue(MarginProperty, new Thickness(0, 2, 0, 0));
        drop.SetBinding(MinWidthProperty, new System.Windows.Data.Binding("ActualWidth") { RelativeSource = tp });
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        drop.AppendChild(scroll);
        popup.AppendChild(drop);
        root.AppendChild(popup);

        template.VisualTree = root;
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    // ── 通用 ──────────────────────────────────────────────────

    private UIElement BuildGeneral()
    {
        var p = Page(L.T("通用", "General"));

        p.Children.Add(Section(L.T("启动", "Startup")));
        var startup = new StackPanel();
        startup.Children.Add(Row(L.T("开机自启", "Launch at Startup"), Toggle(Autostart.IsEnabled(),
            v => { if (v) Autostart.Enable(App.LaunchModeArgs, Config.FastAutostart); else Autostart.Disable(); })));
        startup.Children.Add(Separator());
        startup.Children.Add(Row(L.T("加速自启动", "Fast Startup"), Toggle(Config.FastAutostart, v =>
        {
            Config.FastAutostart = v;
            Config.Save();
            if (Autostart.IsEnabled()) Autostart.Enable(App.LaunchModeArgs, v); // 就地切换机制
        }), L.T("用计划任务代替启动项，登录后立即启动（跳过 Windows 对启动应用的排队延迟）", "Use a scheduled task instead of a Run entry: starts right at logon, skipping the Windows startup-app queue")));
        p.Children.Add(Card(startup));

        p.Children.Add(Section(L.T("语言与交互", "Language & Interaction")));
        var interact = new StackPanel();
        interact.Children.Add(Row(L.T("空格预览文件", "Space to Preview"), Toggle(Config.SpacePreview, v =>
        {
            Config.SpacePreview = v;
            Config.Save();
        }), L.T("选中文件后按空格，调用已安装的第三方预览器（QuickLook / Seer / PowerToys Peek 0.95+）。\n没装任何预览器时空格无效（不影响首字母定位）。", "With a file selected, press Space to invoke an installed third-party previewer (QuickLook / Seer / PowerToys Peek 0.95+).\nDoes nothing if none is installed (type-ahead selection still works).")));
        interact.Children.Add(Separator());
        var langBox = new ComboBox { Width = 150, Background = FieldBg, Foreground = TextFg, BorderBrush = FieldBorder };
        var langKeys = new[] { "auto", "zh", "en" };
        langBox.Items.Add(L.T("跟随系统", "Follow System"));
        langBox.Items.Add("简体中文");
        langBox.Items.Add("English");
        langBox.SelectedIndex = Math.Max(0, Array.IndexOf(langKeys, Config.Language));
        langBox.SelectionChanged += (_, _) =>
        {
            if (langBox.SelectedIndex < 0) return;
            Config.Language = langKeys[langBox.SelectedIndex];
            Config.Save();
        };
        interact.Children.Add(Row(L.T("语言 / Language", "Language / 语言"), langBox,
            L.T("重启 MacDesk 生效", "Takes effect after restarting MacDesk")));
        p.Children.Add(Card(interact));

        return p;
    }

    // ── 桌面 ──────────────────────────────────────────────────

    private UIElement BuildDesktop()
    {
        var p = Page(L.T("桌面", "Desktop"));

        p.Children.Add(Section(L.T("布局", "Layout")));
        var layout = new StackPanel();
        layout.Children.Add(Row(L.T("首行下沉", "Sink First Row"), Toggle(Config.FirstRowSink, v =>
        {
            Config.FirstRowSink = v;
            Config.Save();
            Desktop.LayoutAllWindows(animated: true);
        }), L.T("图标网格整体下移半行，给顶部菜单栏类软件让出空间，首行图标不再被吸顶窗口压住。\n自由摆放模式下手动摆好的图标不动，只影响自动排布；屏幕太矮放不下一行时自动忽略。",
            "Shifts the icon grid down half a row to make room for top menu-bar apps, keeping the first row clear of docked bars.\nIn free placement, manually placed icons stay put - only auto-flow is affected. Ignored when the screen is too short for a row.")));
        p.Children.Add(Card(layout));

        // 系统图标（Windows"桌面图标设置"那一组虚拟项；首启默认跟随原生桌面，之后以此为准）。
        // 5 个同质布尔各占整行太费竖向空间，压成 2 列网格
        p.Children.Add(Section(L.T("系统图标", "System Icons")));
        var deskIcons = new StackPanel();
        var iconGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        void IconCell(string zh, string en, Func<bool> get, Action<bool> set)
        {
            var g = new Grid { Margin = new Thickness(0, 9, 28, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = L.T(zh, en), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var tg = Toggle(get(), v =>
            {
                set(v);
                Config.Save();
                Desktop.RefreshAll(); // 即时增删图标；布局条目保留，重新开启原位回归
            });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(tg, 1);
            g.Children.Add(label);
            g.Children.Add(tg);
            iconGrid.Children.Add(g);
        }
        IconCell("回收站", "Recycle Bin", () => Config.ShowRecycleBin, v => Config.ShowRecycleBin = v);
        IconCell("此电脑", "This PC", () => Config.ShowThisPC, v => Config.ShowThisPC = v);
        IconCell("用户文件", "User's Files", () => Config.ShowUserFiles, v => Config.ShowUserFiles = v);
        IconCell("网络", "Network", () => Config.ShowNetwork, v => Config.ShowNetwork = v);
        IconCell("控制面板", "Control Panel", () => Config.ShowControlPanel, v => Config.ShowControlPanel = v);
        deskIcons.Children.Add(iconGrid);
        deskIcons.Children.Add(new TextBlock
        {
            Text = L.T("对应 Windows「桌面图标设置」；首次启动默认跟随原生桌面。", "The Windows \"Desktop Icon Settings\" set; the first run follows the native desktop."),
            FontSize = 11,
            Foreground = Subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        p.Children.Add(Card(deskIcons));

        p.Children.Add(Section(L.T("布局备份", "Layout Backup")));
        var layoutSec = new StackPanel();
        var importBtn = new Button { Content = L.T("导入…", "Import…"), Padding = new Thickness(14, 4, 14, 4) };
        importBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(L.T("把原生 Windows 桌面的图标摆放导入 MacDesk？\n同名图标的当前位置会被覆盖。", "Import the native Windows desktop arrangement into MacDesk?\nCurrent positions of icons with the same name will be overwritten."),
                    L.T("导入原生桌面布局", "Import Native Desktop Layout"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var native = Interop.NativeDesktopLayout.Read();
                int n = Interop.NativeDesktopLayout.Import(native, Desktop.Monitors, Desktop.Layout, Desktop.Provider.Enumerate());
                Desktop.RefreshAll();
                Desktop.LayoutAllWindows(animated: true);
                MessageBox.Show(L.T($"已导入 {n} 个图标的位置。", $"Imported positions for {n} icons."), "MacDesk");
            }
            catch (Exception ex) { MessageBox.Show(L.T("导入失败：", "Import failed: ") + ex.Message, "MacDesk"); }
        };
        var exportBtn = new Button { Content = L.T("导出…", "Export…"), Padding = new Thickness(14, 4, 14, 4) };
        exportBtn.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog
            {
                Title = L.T("导出 MacDesk 布局", "Export MacDesk Layout"),
                Filter = L.T("MacDesk 布局 (*.json)|*.json", "MacDesk layout (*.json)|*.json"),
                FileName = $"MacDesk-layout-{DateTime.Now:yyyyMMdd}.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                Desktop.Layout.Export(dlg.FileName);
                MessageBox.Show(L.T("布局已导出。", "Layout exported."), "MacDesk");
            }
            catch (Exception ex) { MessageBox.Show(L.T("导出失败：", "Export failed: ") + ex.Message, "MacDesk"); }
        };
        var importLayoutBtn = new Button { Content = L.T("导入…", "Import…"), Padding = new Thickness(14, 4, 14, 4) };
        importLayoutBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = L.T("导入 MacDesk 布局", "Import MacDesk Layout"),
                Filter = L.T("MacDesk 布局 (*.json)|*.json|所有文件 (*.*)|*.*", "MacDesk layout (*.json)|*.json|All files (*.*)|*.*"),
            };
            if (dlg.ShowDialog() != true) return;
            if (MessageBox.Show(L.T("导入将替换当前布局（当前布局会先自动备份）。\n本机不存在的项目会显示为问号占位，可右键移除。", "Importing replaces the current layout (it is backed up automatically first).\nItems that do not exist on this machine appear as question-mark placeholders you can remove."),
                    L.T("导入布局", "Import Layout"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (Desktop.Layout.TryImport(dlg.FileName))
            {
                Desktop.OnLayoutImported();
                MessageBox.Show(L.T("布局已导入。", "Layout imported."), "MacDesk");
            }
            else
                MessageBox.Show(L.T("导入失败：文件不是有效的 MacDesk 布局。当前布局未受影响。", "Import failed: not a valid MacDesk layout file. The current layout is untouched."), "MacDesk",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        // 同类动作并排放一行（导出/导入是一对），别一键一行拉长页面；原生导入语义不同（一次性迁移）单独成行
        var backupBtns = new StackPanel { Orientation = Orientation.Horizontal };
        exportBtn.Margin = new Thickness(0, 0, 8, 0);
        backupBtns.Children.Add(exportBtn);
        backupBtns.Children.Add(importLayoutBtn);
        layoutSec.Children.Add(Row(L.T("备份与恢复", "Back Up & Restore"), backupBtns,
            L.T("导出把当前图标布局存成文件；导入从布局文件恢复摆放（当前布局会先自动备份）", "Export saves the current icon layout to a file; Import restores from one (the current layout is backed up automatically first)")));
        layoutSec.Children.Add(Separator());
        layoutSec.Children.Add(Row(L.T("导入原生桌面布局", "Import Native Desktop Layout"), importBtn, L.T("读取隐藏的原生桌面图标位置并应用到 MacDesk", "Read the hidden native desktop icon positions and apply them to MacDesk")));
        p.Children.Add(Card(layoutSec));

        return p;
    }

    // ── 高级 ──────────────────────────────────────────────────

    private UIElement BuildAdvanced()
    {
        var p = Page(L.T("高级", "Advanced"));

        p.Children.Add(Section(L.T("排障", "Troubleshooting")));
        var trouble = new StackPanel();
        var renderBox = new ComboBox { Width = 150, Background = FieldBg, Foreground = TextFg, BorderBrush = FieldBorder };
        var renderKeys = new[] { "auto", "hardware", "software" };
        renderBox.Items.Add(L.T("自动（推荐）", "Auto (recommended)"));
        renderBox.Items.Add(L.T("强制硬件", "Force hardware"));
        renderBox.Items.Add(L.T("强制软件", "Force software"));
        renderBox.SelectedIndex = Math.Max(0, Array.IndexOf(renderKeys, Config.RenderMode));
        renderBox.SelectionChanged += (_, _) =>
        {
            if (renderBox.SelectedIndex < 0) return;
            Config.RenderMode = renderKeys[renderBox.SelectedIndex];
            Config.Save();
        };
        trouble.Children.Add(Row(L.T("渲染方式", "Rendering"), renderBox,
            L.T("自动 = 检测到老款 Intel 核显（HD/UHD 6xx、Iris Plus 等）时改用软件渲染——这批核显的驱动会把壁纸亮部烧成噪点/白块（issue #1），其余机器保持硬件渲染。\n壁纸仍异常可选「强制软件」。重启 MacDesk 生效。",
                "Auto = switches to software rendering when a legacy Intel iGPU is detected (HD/UHD 6xx, Iris Plus, etc. — their drivers burn wallpaper highlights into speckles/white, issue #1); all other machines stay hardware-rendered.\nPick \"Force software\" if the wallpaper still looks wrong. Takes effect after restarting MacDesk.")));
        trouble.Children.Add(Separator());
        trouble.Children.Add(Row(L.T("菜单在主进程弹出", "Menus in Main Process"), Toggle(Config.MenuInMainProcess,
            v => { Config.MenuInMainProcess = v; Config.Save(); }),
            L.T("推荐开启；关闭需重启 MacDesk 生效", "Recommended on; turning off takes effect after restarting MacDesk")));
        p.Children.Add(Card(trouble));

        p.Children.Add(Section(L.T("调试", "Debug")));
        var advanced = new StackPanel();
        advanced.Children.Add(Row(L.T("显示原生桌面图标", "Show Native Desktop Icons"), Toggle(Interop.DesktopLayer.NativeIconsVisible,
            v => Interop.DesktopLayer.SetNativeIconsVisible(v)),
            L.T("调试用：原生图标在 MacDesk 层下面，当前不透明背景下开了也看不见", "Debug: native icons sit under the MacDesk layer and stay hidden behind the opaque background")));
        p.Children.Add(Card(advanced));

        p.Children.Add(Section(L.T("退出", "Quit")));
        var quitSec = new StackPanel();
        var quitBtn = new Button
        {
            Content = L.T("退出 MacDesk", "Quit MacDesk"),
            Padding = new Thickness(14, 4, 14, 4),
            Foreground = DangerFg,
        };
        quitBtn.Click += (_, _) => App.BeginUserQuit();
        quitSec.Children.Add(Row(L.T("退出", "Quit"), quitBtn, L.T("还原原生桌面图标并停止 MacDesk（快捷键 Ctrl+Alt+Q）", "Restore the native desktop icons and stop MacDesk (hotkey Ctrl+Alt+Q)")));
        p.Children.Add(Card(quitSec));

        return p;
    }

    // ── 外观 ──────────────────────────────────────────────────

    private UIElement BuildAppearance()
    {
        var p = Page(L.T("外观", "Appearance"));

        // 页内按使用频率排：图标大小是本窗口里被反复动得最多的设置，置顶
        p.Children.Add(Section(L.T("图标", "Icons")));
        var sizeSec = new StackPanel();
        sizeSec.Children.Add(Row(L.T("图标大小", "Icon Size"), IconSizeSlider(),
            L.T("拖动可无级调整桌面图标大小，靠近默认时自动吸附；也可按 Ctrl 加 +/- 逐档调整。", "Drag to size desktop icons continuously; snaps to the default when near it. Or press Ctrl with +/- to step through sizes.")));
        p.Children.Add(Card(sizeSec));

        p.Children.Add(Section(L.T("颜色", "Color")));
        var sec = new StackPanel();
        var palette = new StackPanel { Orientation = Orientation.Horizontal };
        void Rebuild()
        {
            palette.Children.Clear();
            foreach (var (key, name, color) in Accent.Palette)
            {
                bool active = (Config.AccentColor ?? "blue") == key;
                var dot = new Border
                {
                    Width = 26, Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(0, 0, 10, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                    BorderBrush = active ? AccentRingSelected : Brushes.Transparent,
                    BorderThickness = new Thickness(2.5),
                };
                if (active)
                    dot.Child = new TextBlock
                    {
                        Text = "✓", Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                        FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    };
                string k = key;
                dot.MouseLeftButtonUp += (_, _) => { Accent.Set(k); Rebuild(); };
                palette.Children.Add(dot);
            }
        }
        Rebuild();
        sec.Children.Add(Row(L.T("强调色", "Accent Color"), palette, L.T("选中标签与框选的颜色，即时生效", "Color of selected labels and the marquee; applies immediately")));
        p.Children.Add(Card(sec));

        p.Children.Add(Section(L.T("壁纸", "Wallpaper")));
        var wall = new StackPanel();
        // 两个"使用动态壁纸时…"从属开关只在主开关开启时有意义：缩进 + 联动置灰表达依赖关系
        //（禁用的 UIElement 收不到鼠标事件，自绘 Toggle 不用自己判 IsEnabled）
        var wallDeps = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = Config.DynamicWallpaper,
            Opacity = Config.DynamicWallpaper ? 1 : 0.45,
        };
        wall.Children.Add(Row(L.T("动态壁纸（Wallpaper Engine）", "Live Wallpaper (Wallpaper Engine)"), Toggle(Config.DynamicWallpaper, v =>
        {
            Config.DynamicWallpaper = v;
            Config.Save();
            foreach (var w in Desktop.Windows) w.ApplyWallpaperMode();
            wallDeps.IsEnabled = v;
            wallDeps.Opacity = v ? 1 : 0.45;
        }), L.T("检测到 Wallpaper Engine 时把动态壁纸接入 MacDesk 桌面层（原生渲染，零额外开销）。\n未运行 Wallpaper Engine 时无影响，显示系统静态壁纸。", "When Wallpaper Engine is detected, its live wallpaper is adopted into the MacDesk desktop layer (native rendering, zero extra cost).\nWithout Wallpaper Engine running this has no effect; the system static wallpaper is shown.")));
        wall.Children.Add(Separator());
        wallDeps.Children.Add(Row(L.T("使用动态壁纸时禁用图标阴影", "Disable Icon Shadows with Live Wallpaper"), Toggle(Config.DynamicNoShadows, v =>
        {
            Config.DynamicNoShadows = v;
            Config.Save();
            foreach (var w in Desktop.Windows) w.RefreshDynamicPerf();
        }), L.T("动态壁纸下图标层由 CPU 软件渲染，阴影是最大开销——与显卡强弱无关，分辨率越高越贵。桌面交互卡顿时请保持开启", "With live wallpaper the icon layer is CPU-rendered; shadows dominate the frame cost regardless of GPU power, and higher resolutions cost more. Keep this on if desktop interactions stutter")));
        wallDeps.Children.Add(Separator());
        wallDeps.Children.Add(Row(L.T("使用动态壁纸时禁用动画", "Disable Animations with Live Wallpaper"), Toggle(Config.DynamicNoAnimations, v =>
        {
            Config.DynamicNoAnimations = v;
            Config.Save();
        }), L.T("展开叠放、整理等布局动画改为瞬移，低配机的帧率保底选项", "Layout animations (stack expand, clean up) become instant moves - a frame-rate floor for low-end machines")));
        wall.Children.Add(wallDeps);
        wall.Children.Add(Separator());
        wall.Children.Add(Row(L.T("静态壁纸", "Static Wallpaper"), new TextBlock { Text = L.T("跟随系统", "Follows System"), Foreground = Subtle, FontSize = 13 },
            L.T("在 Windows 个性化里换壁纸，MacDesk 会自动跟随（含每屏不同壁纸与适配模式）", "Change wallpaper in Windows Personalization; MacDesk follows automatically (per-monitor wallpapers and fit modes included)")));
        p.Children.Add(Card(wall));

        return p;
    }

    // ── 右键菜单 ──────────────────────────────────────────────

    private UIElement BuildMenuPage()
    {
        var p = Page(L.T("右键菜单", "Context Menu"));

        // ── 空白处原生菜单开关 ──
        var nativeSec = new StackPanel();
        nativeSec.Children.Add(Row(L.T("空白处使用 Windows 原生菜单", "Native Windows Menu on Empty Desktop"),
            Toggle(Config.NativeBackgroundMenu, v => { Config.NativeBackgroundMenu = v; Config.Save(); }),
            L.T("桌面空白处右键改为弹 Windows 原生桌面菜单", "Right-click empty desktop shows the native Windows desktop menu")));
        nativeSec.Children.Add(Separator());
        nativeSec.Children.Add(new TextBlock
        {
            Text = L.T(
                "操作逻辑（开启后）：\n" +
                "• 空白处右键 = Windows 原生菜单（Explorer 弹它自己的现代或经典菜单，取决于你的系统设置——我们不重建，你系统里是哪款就出哪款）。\n" +
                "• 按住 Alt 再右键 = MacDesk 自制菜单（整理、排序方式、使用叠放、更换壁纸、设置）。\n" +
                "• 图标上的右键不受影响，始终是原生 shell 菜单。\n" +
                "注意：原生菜单里的\"查看\"\"排序方式\"\"显示桌面图标\"等作用于被隐藏的原生图标层，对 MacDesk 的图标不起作用——要排列 MacDesk 图标，用 Alt 菜单里的整理 / 排序方式 / 使用叠放。",
                "How it works (when on):\n" +
                "• Right-click empty desktop = the native Windows menu (Explorer shows its own modern or classic menu per your system — we don't rebuild it, you get whichever your system uses).\n" +
                "• Hold Alt and right-click = the MacDesk menu (Clean Up, Sort By, Use Stacks, Change Wallpaper, Settings).\n" +
                "• Right-clicking an icon is unaffected and always shows the native shell menu.\n" +
                "Note: the native menu's View / Sort by / Show desktop icons act on the hidden native icon layer and do nothing to MacDesk icons — to arrange MacDesk icons use Clean Up / Sort By / Use Stacks in the Alt menu."),
            FontSize = 11,
            Foreground = Subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 6),
        });
        p.Children.Add(Card(nativeSec));

        // 每次建页都新建控件：字段单例会滞留在上一次页面的可视树里，重挂载抛
        // "already the logical child" 被兜底吞掉 → 页面点不进去（机主实测 bug）
        var blacklist = new ListBox { Height = 150, BorderThickness = new Thickness(0), Background = FieldBg, Foreground = TextFg };
        void RefreshList()
        {
            blacklist.Items.Clear();
            foreach (var b in Config.MenuBlacklist) blacklist.Items.Add(b);
        }

        var bl = new StackPanel();
        bl.Children.Add(new TextBlock
        {
            Text = L.T("屏蔽菜单项：右键菜单里文本包含以下任一子串的项会被移除（不分大小写，下次弹菜单生效）。", "Blocked menu items: context-menu entries whose text contains any of these substrings are removed (case-insensitive, applies to the next menu)."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Subtle,
            Margin = new Thickness(0, 10, 0, 8),
        });
        RefreshList();
        bl.Children.Add(new Border
        {
            BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = blacklist,
        });

        var pickRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var pick = new ComboBox { Margin = new Thickness(0, 0, 6, 0), Background = FieldBg, Foreground = TextFg, BorderBrush = FieldBorder };
        var pickBtn = new Button { Content = L.T("屏蔽该项", "Block Item"), Width = 88, Padding = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(pickBtn, Dock.Right);
        pickRow.Children.Add(pickBtn);
        pickRow.Children.Add(pick);
        bl.Children.Add(pickRow);

        void FillPick()
        {
            var have = new HashSet<string>(Config.MenuBlacklist, StringComparer.OrdinalIgnoreCase);
            pick.ItemsSource = NativeMenuPresenter.MenuItemCatalog.Where(t => !have.Contains(t)).ToList();
        }
        FillPick();
        pick.DropDownOpened += (_, _) => FillPick();

        var addRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var input = new TextBox { Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(2), Background = FieldBg, Foreground = TextFg, BorderBrush = FieldBorder };
        var addBtn = new Button { Content = L.T("添加", "Add"), Width = 64, Padding = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(addBtn, Dock.Right);
        addRow.Children.Add(addBtn);
        addRow.Children.Add(input);
        bl.Children.Add(addRow);

        var delBtn = new Button
        {
            Content = L.T("删除选中", "Remove Selected"),
            Width = 88,
            Padding = new Thickness(0, 3, 0, 3),
            Margin = new Thickness(0, 6, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        bl.Children.Add(delBtn);

        void Add(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return;
            if (!Config.MenuBlacklist.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                Config.MenuBlacklist.Add(s);
                Config.Save();
                RefreshList();
            }
            FillPick();
        }
        addBtn.Click += (_, _) => { Add(input.Text); input.Clear(); input.Focus(); };
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Add(input.Text); input.Clear(); } };
        pickBtn.Click += (_, _) => { if (pick.SelectedItem is string s) Add(s); };
        delBtn.Click += (_, _) =>
        {
            if (blacklist.SelectedItem is not string sel) return;
            Config.MenuBlacklist.RemoveAll(b => string.Equals(b, sel, StringComparison.OrdinalIgnoreCase));
            Config.Save();
            RefreshList();
            FillPick();
        };
        p.Children.Add(Card(bl));

        return p;
    }

    // ── 关于 ──────────────────────────────────────────────────

    private UIElement BuildAbout()
    {
        var p = Page(L.T("关于", "About"));

        var about = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 6) };
        try
        {
            about.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/macdesk_1024.png")),
                Width = 84, Height = 84,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
        catch { }
        about.Children.Add(new TextBlock
        {
            Text = "MacDesk",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        about.Children.Add(new TextBlock
        {
            Text = L.T($"版本 {UpdateCheck.CurrentVersion}", $"Version {UpdateCheck.CurrentVersion}"),
            FontSize = 12,
            Foreground = Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = L.T("mac 式 Windows 桌面图标层", "A macOS-style desktop icon layer for Windows"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = "由 Claude 开发 · Built by Claude (Anthropic)",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = L.T("无后端 · 无遥测 · 除手动检查更新外不联网", "No backend · No telemetry · Only goes online for manual update checks"),
            FontSize = 11,
            Foreground = Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 16),
        });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var gh = new Button { Content = "GitHub", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 10, 0) };
        gh.Click += (_, _) => OpenUrl("https://github.com/Nishikinonakai/MacDesk");
        var upd = new Button { Content = L.T("检查更新", "Check for Updates"), Padding = new Thickness(16, 5, 16, 5) };
        var status = new TextBlock
        {
            FontSize = 12, Foreground = Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        };
        upd.Click += async (_, _) =>
        {
            upd.IsEnabled = false;
            status.Text = L.T("检查中…", "Checking…");
            var (has, msg, url, tag) = await UpdateCheck.Run();
            status.Text = msg;
            if (!has || tag == null) { upd.IsEnabled = true; return; }
            var r = MessageBox.Show(msg + L.T("\n\n是 = 自动下载并安装（装完自动重启 MacDesk）\n否 = 打开下载页手动更新", "\n\nYes = download and install automatically (MacDesk restarts when done)\nNo = open the download page for a manual update"),
                L.T("MacDesk 更新", "MacDesk Update"), MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (r == MessageBoxResult.No && url != null) OpenUrl(url);
            else if (r == MessageBoxResult.Yes)
            {
                try
                {
                    string setup = await UpdateCheck.DownloadSetup(tag, p => status.Text = L.T($"下载更新… {p}%", $"Downloading update… {p}%"));
                    status.Text = L.T("安装中…（MacDesk 将自动重启）", "Installing… (MacDesk will restart automatically)");
                    // 安装器会 --quit 我们 → 还原原生图标 → 换文件 → /RELAUNCH 拉起新版本
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/VERYSILENT /RELAUNCH=1")
                    { UseShellExecute = true });
                }
                catch (Exception ex) { status.Text = L.T("下载失败：", "Download failed: ") + ex.Message; }
            }
            upd.IsEnabled = true;
        };
        var diag = new Button { Content = L.T("导出诊断包…", "Export Diagnostics…"), Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(10, 0, 0, 0) };
        diag.Click += (_, _) => ExportDiagnostics();
        btns.Children.Add(gh);
        btns.Children.Add(upd);
        btns.Children.Add(diag);
        about.Children.Add(btns);
        about.Children.Add(status);

        p.Children.Add(Card(about));
        return p;
    }

    /// <summary>诊断包 = 日志 + 设置 + 布局档 + 环境摘要，zip 落在用户选的位置。
    /// 只在用户主动点击时生成、绝不自动上传（与关于页"无遥测"承诺一致）；
    /// 弹窗明示内容含桌面文件名等个人信息，发出去前用户可自查。</summary>
    private static void ExportDiagnostics()
    {
        if (MessageBox.Show(
                L.T("诊断包用于向开发者反馈问题，将打包以下本机文件：\n\n" +
                    "• 运行日志（含桌面文件名、显示器型号等使用痕迹）\n" +
                    "• 设置 settings.json\n" +
                    "• 布局档 layout.json（桌面文件名与位置）\n" +
                    "• 环境摘要（系统版本、显示器、MacDesk 版本）\n\n" +
                    "不含任何文件内容，也不会自动上传——zip 保存在你选的位置，\n发送前可自行检查删改。继续导出？",
                    "The diagnostics bundle is for reporting problems to the developer. It packages these local files:\n\n" +
                    "• Run logs (contain traces like desktop file names and monitor models)\n" +
                    "• settings.json\n" +
                    "• layout.json (desktop file names and positions)\n" +
                    "• Environment summary (OS version, monitors, MacDesk version)\n\n" +
                    "No file contents are included and nothing is uploaded - the zip is saved where you choose,\nreview or edit it before sending. Export now?"),
                L.T("导出诊断包", "Export Diagnostics"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var dlg = new SaveFileDialog
        {
            FileName = $"MacDesk-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = L.T("Zip 压缩包|*.zip", "Zip archive|*.zip"),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDesk");
            using var zipStream = new FileStream(dlg.FileName, FileMode.Create);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);
            void AddFile(string path, string entryName)
            {
                if (!File.Exists(path)) return;
                using var es = zip.CreateEntry(entryName).Open();
                // 日志/布局可能正被主进程写着，共享读打开
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.CopyTo(es);
            }
            AddFile(Log.FilePath, "macdesk.log");
            AddFile(Log.FilePath + ".1", "macdesk.log.1");
            AddFile(Path.Combine(dataDir, "settings.json"), "settings.json");
            AddFile(Path.Combine(dataDir, "layout.json"), "layout.json");
            using (var w = new StreamWriter(zip.CreateEntry("environment.txt").Open()))
            {
                w.WriteLine($"MacDesk {UpdateCheck.CurrentVersion}");
                w.WriteLine($"OS: {Environment.OSVersion}{(Environment.Is64BitOperatingSystem ? " x64" : "")}");
                w.WriteLine($".NET: {Environment.Version}");
                w.WriteLine($"Launch args: {string.Join(" ", App.LaunchModeArgs)}");
                w.WriteLine($"Settings: free={Config.FreePlacement} stacks={Config.UseStacks}/{Config.StackGroupBy} " +
                            $"menuMain={Config.MenuInMainProcess} dynamicWallpaper={Config.DynamicWallpaper} fastAutostart={Config.FastAutostart}");
                foreach (var m in Desktop.Monitors)
                    w.WriteLine($"Monitor: {m.Key}{(m.IsPrimary ? " (primary)" : "")} " +
                                $"({m.Physical.Left},{m.Physical.Top}) {m.Physical.Width}x{m.Physical.Height} dpi={m.Dpi}");
            }
            MessageBox.Show(L.T("诊断包已保存：\n", "Diagnostics saved:\n") + dlg.FileName, "MacDesk");
        }
        catch (Exception ex) { MessageBox.Show(L.T("导出失败：", "Export failed: ") + ex.Message, "MacDesk"); }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("open url failed: " + ex.Message); }
    }
}
