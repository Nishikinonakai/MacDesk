using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDesk.Services;
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

    // mac 系统设置观感的常量
    private static readonly Brush SidebarBg = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
    private static readonly Brush ContentBg = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF9));
    private static readonly Brush CardBg = Brushes.White;
    private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6));
    private static readonly Brush Subtle = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73));

    private readonly ContentControl _page = new();
    private readonly ListBox _nav = new();

    private SettingsWindow()
    {
        Title = "MacDesk 设置";
        Width = 760;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = ContentBg;
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

    private static Style NavItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 7, 8, 7)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
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

    private static Border Card(UIElement content) => new()
    {
        Background = CardBg,
        BorderBrush = CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 6, 16, 6),
        Margin = new Thickness(0, 0, 0, 14),
        Child = content,
    };

    private static UIElement Row(string label, UIElement control, string? hint = null)
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

    private static Border Separator() => new()
    {
        Height = 1,
        Background = CardBorder,
        Margin = new Thickness(-16, 0, -16, 0),
    };

    private static CheckBox Toggle(bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox { IsChecked = initial };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    // ── 通用 ──────────────────────────────────────────────────

    private UIElement BuildGeneral()
    {
        var p = Page("通用");

        var startup = new StackPanel();
        startup.Children.Add(Row("开机自启", Toggle(Autostart.IsEnabled(),
            v => { if (v) Autostart.Enable(App.LaunchModeArgs); else Autostart.Disable(); })));
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x2B, 0x2B)),
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
                    BorderBrush = active ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x38)) : Brushes.Transparent,
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

        var note = new StackPanel();
        note.Children.Add(Row("壁纸", new TextBlock { Text = "跟随系统", Foreground = Subtle, FontSize = 13 },
            "在 Windows 个性化里换壁纸，MacDesk 会自动跟随（含每屏不同壁纸与适配模式）"));
        p.Children.Add(Card(note));

        return p;
    }

    // ── 右键菜单 ──────────────────────────────────────────────

    private readonly ListBox _blacklist = new() { Height = 150, BorderThickness = new Thickness(0) };

    private UIElement BuildMenuPage()
    {
        var p = Page("右键菜单");

        var bl = new StackPanel();
        bl.Children.Add(new TextBlock
        {
            Text = "屏蔽菜单项：右键菜单里文本包含以下任一子串的项会被移除（不分大小写，下次弹菜单生效）。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Subtle,
            Margin = new Thickness(0, 10, 0, 8),
        });
        RefreshBlacklist();
        bl.Children.Add(new Border
        {
            BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = _blacklist,
        });

        var pickRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var pick = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
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
        var input = new TextBox { Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(2) };
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
                RefreshBlacklist();
            }
            FillPick();
        }
        addBtn.Click += (_, _) => { Add(input.Text); input.Clear(); input.Focus(); };
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Add(input.Text); input.Clear(); } };
        pickBtn.Click += (_, _) => { if (pick.SelectedItem is string s) Add(s); };
        delBtn.Click += (_, _) =>
        {
            if (_blacklist.SelectedItem is not string sel) return;
            Config.MenuBlacklist.RemoveAll(b => string.Equals(b, sel, StringComparison.OrdinalIgnoreCase));
            Config.Save();
            RefreshBlacklist();
            FillPick();
        };
        p.Children.Add(Card(bl));

        return p;
    }

    private void RefreshBlacklist()
    {
        _blacklist.Items.Clear();
        foreach (var b in Config.MenuBlacklist) _blacklist.Items.Add(b);
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
            var (has, msg, url) = await UpdateCheck.Run();
            status.Text = msg;
            upd.IsEnabled = true;
            if (has && url != null &&
                MessageBox.Show(msg + "\n\n前往下载页？", "MacDesk 更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenUrl(url);
        };
        btns.Children.Add(gh);
        btns.Children.Add(upd);
        about.Children.Add(btns);
        about.Children.Add(status);

        p.Children.Add(Card(about));
        return p;
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
