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

        Title = "MacDesk 设置";
        Width = 760;
        Height = 540;
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
        foreach (var (icon, name) in new[] { ("⚙️", "通用"), ("🎨", "外观"), ("📋", "右键菜单"), ("ℹ️", "关于") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = name });
        }
        _nav.SelectionChanged += (_, _) => ShowPage(((ListBoxItem)_nav.SelectedItem).Tag as string ?? "通用");
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

    private void ShowPage(string name) => _page.Content = name switch
    {
        "外观" => BuildAppearance(),
        "右键菜单" => BuildMenuPage(),
        "关于" => BuildAbout(),
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
        var p = Page("通用");

        var startup = new StackPanel();
        startup.Children.Add(Row("开机自启", Toggle(Autostart.IsEnabled(),
            v => { if (v) Autostart.Enable(App.LaunchModeArgs, Config.FastAutostart); else Autostart.Disable(); })));
        startup.Children.Add(Separator());
        startup.Children.Add(Row("加速自启动", Toggle(Config.FastAutostart, v =>
        {
            Config.FastAutostart = v;
            Config.Save();
            if (Autostart.IsEnabled()) Autostart.Enable(App.LaunchModeArgs, v); // 就地切换机制
        }), "用计划任务代替启动项，登录后立即启动（跳过 Windows 对启动应用的排队延迟）"));
        startup.Children.Add(Separator());
        startup.Children.Add(Row("菜单在主进程弹出", Toggle(Config.MenuInMainProcess,
            v => { Config.MenuInMainProcess = v; Config.Save(); }),
            "推荐开启；关闭需重启 MacDesk 生效"));
        p.Children.Add(Card(startup));

        var layoutSec = new StackPanel();
        var importBtn = new Button { Content = "导入…", Padding = new Thickness(14, 4, 14, 4) };
        importBtn.Click += (_, _) =>
        {
            if (MessageBox.Show("把原生 Windows 桌面的图标摆放导入 MacDesk？\n同名图标的当前位置会被覆盖。",
                    "导入原生桌面布局", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var native = Interop.NativeDesktopLayout.Read();
                int n = Interop.NativeDesktopLayout.Import(native, Desktop.Monitors, Desktop.Layout, Desktop.Provider.Enumerate());
                Desktop.RefreshAll();
                Desktop.LayoutAllWindows(animated: true);
                MessageBox.Show($"已导入 {n} 个图标的位置。", "MacDesk");
            }
            catch (Exception ex) { MessageBox.Show("导入失败：" + ex.Message, "MacDesk"); }
        };
        layoutSec.Children.Add(Row("导入原生桌面布局", importBtn, "读取隐藏的原生桌面图标位置并应用到 MacDesk"));

        layoutSec.Children.Add(Separator());
        var exportBtn = new Button { Content = "导出…", Padding = new Thickness(14, 4, 14, 4) };
        exportBtn.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "导出 MacDesk 布局",
                Filter = "MacDesk 布局 (*.json)|*.json",
                FileName = $"MacDesk-layout-{DateTime.Now:yyyyMMdd}.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                Desktop.Layout.Export(dlg.FileName);
                MessageBox.Show("布局已导出。", "MacDesk");
            }
            catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message, "MacDesk"); }
        };
        layoutSec.Children.Add(Row("导出布局", exportBtn, "把当前图标布局存成文件（换机/重装时导入恢复）"));

        layoutSec.Children.Add(Separator());
        var importLayoutBtn = new Button { Content = "导入…", Padding = new Thickness(14, 4, 14, 4) };
        importLayoutBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入 MacDesk 布局",
                Filter = "MacDesk 布局 (*.json)|*.json|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            if (MessageBox.Show("导入将替换当前布局（当前布局会先自动备份）。\n本机不存在的项目会显示为问号占位，可右键移除。",
                    "导入布局", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (Desktop.Layout.TryImport(dlg.FileName))
            {
                Desktop.OnLayoutImported();
                MessageBox.Show("布局已导入。", "MacDesk");
            }
            else
                MessageBox.Show("导入失败：文件不是有效的 MacDesk 布局。当前布局未受影响。", "MacDesk",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        layoutSec.Children.Add(Row("导入布局", importLayoutBtn, "从导出的布局文件恢复图标摆放"));

        p.Children.Add(Card(layoutSec));

        var advanced = new StackPanel();
        advanced.Children.Add(Row("显示原生桌面图标", Toggle(Interop.DesktopLayer.NativeIconsVisible,
            v => Interop.DesktopLayer.SetNativeIconsVisible(v)),
            "调试用：原生图标在 MacDesk 层下面，当前不透明背景下开了也看不见"));
        advanced.Children.Add(Separator());
        var quitBtn = new Button
        {
            Content = "退出 MacDesk",
            Padding = new Thickness(14, 4, 14, 4),
            Foreground = DangerFg,
        };
        quitBtn.Click += (_, _) => App.BeginUserQuit();
        advanced.Children.Add(Row("退出", quitBtn, "还原原生桌面图标并停止 MacDesk（快捷键 Ctrl+Alt+Q）"));
        p.Children.Add(Card(advanced));

        return p;
    }

    // ── 外观 ──────────────────────────────────────────────────

    private UIElement BuildAppearance()
    {
        var p = Page("外观");

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
        sec.Children.Add(Row("强调色", palette, "选中标签与框选的颜色，即时生效"));
        p.Children.Add(Card(sec));

        var wall = new StackPanel();
        wall.Children.Add(Row("动态壁纸（Wallpaper Engine）", Toggle(Config.DynamicWallpaper, v =>
        {
            Config.DynamicWallpaper = v;
            Config.Save();
            foreach (var w in Desktop.Windows) w.ApplyWallpaperMode();
        }), "检测到 Wallpaper Engine 时把动态壁纸接入 MacDesk 桌面层（原生渲染，零额外开销）。\n未运行 Wallpaper Engine 时无影响，显示系统静态壁纸。"));
        wall.Children.Add(Separator());
        wall.Children.Add(Row("使用动态壁纸时禁用图标阴影", Toggle(Config.DynamicNoShadows, v =>
        {
            Config.DynamicNoShadows = v;
            Config.Save();
            foreach (var w in Desktop.Windows) w.RefreshDynamicPerf();
        }), "动态壁纸下图标层改走软件渲染，阴影是性能大头。推荐低配机保持开启；显卡强可关闭保留阴影"));
        wall.Children.Add(Separator());
        wall.Children.Add(Row("使用动态壁纸时禁用动画", Toggle(Config.DynamicNoAnimations, v =>
        {
            Config.DynamicNoAnimations = v;
            Config.Save();
        }), "展开叠放、整理等布局动画改为瞬移，低配机的帧率保底选项"));
        wall.Children.Add(Separator());
        wall.Children.Add(Row("静态壁纸", new TextBlock { Text = "跟随系统", Foreground = Subtle, FontSize = 13 },
            "在 Windows 个性化里换壁纸，MacDesk 会自动跟随（含每屏不同壁纸与适配模式）"));
        p.Children.Add(Card(wall));

        return p;
    }

    // ── 右键菜单 ──────────────────────────────────────────────

    private UIElement BuildMenuPage()
    {
        var p = Page("右键菜单");
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
            Text = "屏蔽菜单项：右键菜单里文本包含以下任一子串的项会被移除（不分大小写，下次弹菜单生效）。",
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
        var pickBtn = new Button { Content = "屏蔽该项", Width = 88, Padding = new Thickness(0, 3, 0, 3) };
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
        var addBtn = new Button { Content = "添加", Width = 64, Padding = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(addBtn, Dock.Right);
        addRow.Children.Add(addBtn);
        addRow.Children.Add(input);
        bl.Children.Add(addRow);

        var delBtn = new Button
        {
            Content = "删除选中",
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
        var p = Page("关于");

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
            Text = $"版本 {UpdateCheck.CurrentVersion}",
            FontSize = 12,
            Foreground = Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        about.Children.Add(new TextBlock
        {
            Text = "mac 式 Windows 桌面图标层",
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
            Text = "无后端 · 无遥测 · 除手动检查更新外不联网",
            FontSize = 11,
            Foreground = Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 16),
        });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var gh = new Button { Content = "GitHub", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 10, 0) };
        gh.Click += (_, _) => OpenUrl("https://github.com/Nishikinonakai/MacDesk");
        var upd = new Button { Content = "检查更新", Padding = new Thickness(16, 5, 16, 5) };
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
            status.Text = "检查中…";
            var (has, msg, url, tag) = await UpdateCheck.Run();
            status.Text = msg;
            if (!has || tag == null) { upd.IsEnabled = true; return; }
            var r = MessageBox.Show(msg + "\n\n是 = 自动下载并安装（装完自动重启 MacDesk）\n否 = 打开下载页手动更新",
                "MacDesk 更新", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (r == MessageBoxResult.No && url != null) OpenUrl(url);
            else if (r == MessageBoxResult.Yes)
            {
                try
                {
                    string setup = await UpdateCheck.DownloadSetup(tag, p => status.Text = $"下载更新… {p}%");
                    status.Text = "安装中…（MacDesk 将自动重启）";
                    // 安装器会 --quit 我们 → 还原原生图标 → 换文件 → /RELAUNCH 拉起新版本
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/VERYSILENT /RELAUNCH=1")
                    { UseShellExecute = true });
                }
                catch (Exception ex) { status.Text = "下载失败：" + ex.Message; }
            }
            upd.IsEnabled = true;
        };
        var diag = new Button { Content = "导出诊断包…", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(10, 0, 0, 0) };
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
                "诊断包用于向开发者反馈问题，将打包以下本机文件：\n\n" +
                "• 运行日志（含桌面文件名、显示器型号等使用痕迹）\n" +
                "• 设置 settings.json\n" +
                "• 布局档 layout.json（桌面文件名与位置）\n" +
                "• 环境摘要（系统版本、显示器、MacDesk 版本）\n\n" +
                "不含任何文件内容，也不会自动上传——zip 保存在你选的位置，\n发送前可自行检查删改。继续导出？",
                "导出诊断包", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var dlg = new SaveFileDialog
        {
            FileName = $"MacDesk-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Zip 压缩包|*.zip",
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
            MessageBox.Show("诊断包已保存：\n" + dlg.FileName, "MacDesk");
        }
        catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message, "MacDesk"); }
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
