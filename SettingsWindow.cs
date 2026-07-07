using System.Windows;
using System.Windows.Controls;
using MacDesk.Services;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using GroupBox = System.Windows.Controls.GroupBox;
using DockPanel = System.Windows.Controls.DockPanel;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MacDesk;

/// <summary>
/// 设置窗口 v1（机主点名：菜单项屏蔽必须有 GUI，settings.json 手编只是过渡）。
/// 代码构 UI、改动即存；共享 Desktop.Config 实例（和右键菜单勾选态同源）。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>单例打开（背景菜单"MacDesk 设置…"入口）。</summary>
    public static void ShowSingleton()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private static Settings Config => Desktop.Config;

    private readonly ListBox _blacklist = new() { Height = 140 };

    private SettingsWindow()
    {
        Title = "MacDesk 设置";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;

        var root = new StackPanel { Margin = new Thickness(16) };

        // ── 常规 ──
        var general = new StackPanel { Margin = new Thickness(8) };
        general.Children.Add(Check("自由摆放（不吸附网格）", Config.FreePlacement, v =>
        {
            Config.FreePlacement = v;
            Config.Save();
            Desktop.LayoutAllWindows(animated: true);
        }));
        general.Children.Add(Check("开机自启", Autostart.IsEnabled(), v =>
        {
            if (v) Autostart.Enable(App.LaunchModeArgs);
            else Autostart.Disable();
        }));
        general.Children.Add(Check("菜单在主进程弹出（推荐；关闭需重启 MacDesk 生效）", Config.MenuInMainProcess, v =>
        {
            Config.MenuInMainProcess = v;
            Config.Save();
        }));
        root.Children.Add(new GroupBox { Header = "常规", Content = general, Margin = new Thickness(0, 0, 0, 12) });

        // ── 菜单项屏蔽 ──
        var bl = new StackPanel { Margin = new Thickness(8) };
        bl.Children.Add(new TextBlock
        {
            Text = "右键菜单里文本包含以下任一子串的项会被移除（不分大小写，下次弹菜单生效）：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = Brushes.DimGray,
        });
        RefreshBlacklist();
        bl.Children.Add(_blacklist);

        // 预设选择：从最近弹过的菜单里直接挑（右键过一次桌面/图标目录就有货）
        var pickRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var pick = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
        var pickBtn = new Button { Content = "屏蔽该项", Width = 88, Padding = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(pickBtn, Dock.Right);
        pickRow.Children.Add(pickBtn);
        pickRow.Children.Add(pick);
        bl.Children.Add(pickRow);

        void FillPick()
        {
            var have = new HashSet<string>(Config.MenuBlacklist, StringComparer.OrdinalIgnoreCase);
            pick.ItemsSource = Services.NativeMenuPresenter.MenuItemCatalog.Where(t => !have.Contains(t)).ToList();
        }
        FillPick();
        pick.DropDownOpened += (_, _) => FillPick(); // 每次展开取最新目录

        var addRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var input = new TextBox { Margin = new Thickness(0, 0, 6, 0) };
        var addBtn = new Button { Content = "添加", Width = 64, Padding = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(addBtn, Dock.Right);
        addRow.Children.Add(addBtn);
        addRow.Children.Add(input);
        bl.Children.Add(addRow);

        var delBtn = new Button
        {
            Content = "删除选中",
            Width = 88,
            Padding = new Thickness(0, 2, 0, 2),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        bl.Children.Add(delBtn);

        void Add()
        {
            var s = input.Text.Trim();
            if (s.Length == 0) return;
            if (!Config.MenuBlacklist.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                Config.MenuBlacklist.Add(s);
                Config.Save();
                RefreshBlacklist();
            }
            input.Clear();
            input.Focus();
        }
        addBtn.Click += (_, _) => Add();
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Add(); };
        pickBtn.Click += (_, _) =>
        {
            if (pick.SelectedItem is not string s || s.Length == 0) return;
            if (!Config.MenuBlacklist.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                Config.MenuBlacklist.Add(s);
                Config.Save();
                RefreshBlacklist();
            }
            FillPick();
        };
        delBtn.Click += (_, _) =>
        {
            if (_blacklist.SelectedItem is not string sel) return;
            Config.MenuBlacklist.RemoveAll(b => string.Equals(b, sel, StringComparison.OrdinalIgnoreCase));
            Config.Save();
            RefreshBlacklist();
        };
        root.Children.Add(new GroupBox { Header = "菜单项屏蔽", Content = bl, Margin = new Thickness(0, 0, 0, 12) });

        // ── 关于 ──
        root.Children.Add(new TextBlock
        {
            Text = "MacDesk — mac 式 Windows 桌面图标层。配置文件：%LOCALAPPDATA%\\MacDesk\\settings.json",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 11,
        });

        Content = root;
    }

    private void RefreshBlacklist()
    {
        _blacklist.Items.Clear();
        foreach (var b in Config.MenuBlacklist) _blacklist.Items.Add(b);
    }

    private static CheckBox Check(string text, bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            IsChecked = initial,
            Margin = new Thickness(0, 4, 0, 4),
        };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }
}
