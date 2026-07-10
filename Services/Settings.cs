using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MacDesk.Services;

/// <summary>轻量用户设置（%LOCALAPPDATA%\MacDesk\settings.json）。</summary>
internal sealed class Settings
{
    private readonly string _file;

    /// <summary>自由摆放：拖放落点即位置、重排不吸附网格（macOS arrangeBy=none）。默认关（Windows 习惯网格）。</summary>
    public bool FreePlacement { get; set; }

    /// <summary>右键菜单黑名单：菜单项文本含任一子串（不分大小写）即被移除。设置 GUI 可增删。
    /// 默认屏蔽：AMD 项（机主点名）+ "授予访问权限"（桌面场景纯噪音的网络共享向导，中英双杀）。</summary>
    public List<string> MenuBlacklist { get; set; } = new() { "AMD Software", "Give access to", "授予访问权限" };

    /// <summary>使用叠放（macOS Use Stacks）：文件按类型聚成堆、自动右上列流，文件夹保持独立。
    /// 开启期间不写规范布局，关闭即恢复原摆放。</summary>
    public bool UseStacks { get; set; }

    /// <summary>叠放的分组依据："kind"（类型，默认）/"date"（修改日期）/"size"（大小）。
    /// 只在 UseStacks 时生效。</summary>
    public string StackGroupBy { get; set; } = "kind";

    /// <summary>菜单序列化进主进程同线程弹出（前台战争终极解）。false = 回退旧 host 内
    /// TrackPopupMenu 路径（settle-wait+重试），新路径出问题时的免重建逃生口。</summary>
    public bool MenuInMainProcess { get; set; } = true;

    /// <summary>强调色 key（见 Accent.Palette），影响选中标签/框选颜色。</summary>
    public string AccentColor { get; set; } = "blue";

    /// <summary>动态壁纸兼容（Wallpaper Engine）：检测到 WE 的每屏渲染窗即收编进桌面层
    /// （三明治 v3：图标呈现层 → WE 窗 → WPF 输入层），WE 原生渲染零开销。默认开——
    /// 没装/没开 WE 时不做任何事，保持静态壁纸镜像。</summary>
    public bool DynamicWallpaper { get; set; } = true;

    /// <summary>动态壁纸时禁用图标阴影：图标层走软件光栅化，DropShadow 是帧成本大头
    /// （4K 实测 1031ms→50ms 的差距）。默认开（低配友好）；高配想要阴影可关。</summary>
    public bool DynamicNoShadows { get; set; } = true;

    /// <summary>动态壁纸时禁用布局动画（展开叠放/整理等改为瞬移）：低配机的帧率保底选项。
    /// 默认关（动画是 MacDesk 的灵魂，帧率能接受就留着）。</summary>
    public bool DynamicNoAnimations { get; set; }

    /// <summary>自启动用计划任务（onlogon 即启）替代 Run 键，绕过 Windows 对启动项的
    /// 串行延迟（机主实测 Run 键要等 ~40s+）。仅记录偏好；实际状态以系统里注册的为准。</summary>
    public bool FastAutostart { get; set; }

    /// <summary>界面语言：auto（跟随系统 UI 语言）| zh | en。重启生效（见 L.cs）。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>选中文件按空格调用第三方预览器（QuickLook/Seer/PowerToys Peek）。默认开——
    /// 没装任何预览器时空格什么都不做（不影响首字母定位，见 FilePreview）。</summary>
    public bool SpacePreview { get; set; } = true;

    /// <summary>空白处右键出 Windows 原生桌面菜单（转发 WM_CONTEXTMENU 给 DefView，Explorer
    /// 弹它自己的现代/经典菜单）；此时按住 Alt 再右键才出 MacDesk 自制菜单。默认关。</summary>
    public bool NativeBackgroundMenu { get; set; }

    /// <summary>图标尺寸（base 图标 DIU，缩放因子 S = IconSize/64）。档位见 MainWindow.IconSizeSteps，
    /// 默认 64。Ctrl +/- 与外观页滑杆调整；不写 Canon（切档=切分辨率同理，仅显示现算）。</summary>
    public int IconSize { get; set; } = 64;

    /// <summary>在桌面上显示回收站图标。未保存过设置时跟随原生桌面：原生桌面上回收站可见则默认开，否则关。</summary>
    public bool ShowRecycleBin { get; set; } = IsRecycleBinVisibleOnNativeDesktop();

    /// <summary>读取原生桌面回收站是否可见（Windows 10+ 注册表）。</summary>
    private static bool IsRecycleBinVisibleOnNativeDesktop()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
            // 值不存在或为 0 = 可见，值为 1 = 隐藏
            return key?.GetValue("{645FF040-5081-101B-9F08-00AA002F954E}") is not int v || v == 0;
        }
        catch { return true; }
    }

    /// <summary>软件渲染（等效 --soft）：整进程绕开显卡走 WPF 软件光栅化。个别核显驱动在
    /// 硬件合成路径把壁纸镜像亮部烧成彩色噪点（issue #1：Intel UHD 630，升最新驱动无效，
    /// 软件渲染实测干净）。默认关（绝大多数显卡正常，软件渲染白费 CPU）。重启生效。</summary>
    public bool SoftwareRender { get; set; }

    private Settings(string file) => _file = file;

    public static Settings Load()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDesk");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        var s = new Settings(file);
        try
        {
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("FreePlacement", out var fp)) s.FreePlacement = fp.GetBoolean();
                if (doc.RootElement.TryGetProperty("MenuBlacklist", out var bl) && bl.ValueKind == JsonValueKind.Array)
                    s.MenuBlacklist = bl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                if (doc.RootElement.TryGetProperty("MenuInMainProcess", out var mm)) s.MenuInMainProcess = mm.GetBoolean();
                if (doc.RootElement.TryGetProperty("AccentColor", out var ac) && ac.ValueKind == JsonValueKind.String)
                    s.AccentColor = ac.GetString()!;
                if (doc.RootElement.TryGetProperty("UseStacks", out var us)) s.UseStacks = us.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicWallpaper", out var dw)) s.DynamicWallpaper = dw.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicNoShadows", out var ns)) s.DynamicNoShadows = ns.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicNoAnimations", out var na)) s.DynamicNoAnimations = na.GetBoolean();
                if (doc.RootElement.TryGetProperty("FastAutostart", out var fa)) s.FastAutostart = fa.GetBoolean();
                if (doc.RootElement.TryGetProperty("StackGroupBy", out var gb) && gb.ValueKind == JsonValueKind.String)
                    s.StackGroupBy = gb.GetString()!;
                if (doc.RootElement.TryGetProperty("Language", out var lg) && lg.ValueKind == JsonValueKind.String)
                    s.Language = lg.GetString()!;
                if (doc.RootElement.TryGetProperty("SpacePreview", out var sp)) s.SpacePreview = sp.GetBoolean();
                if (doc.RootElement.TryGetProperty("NativeBackgroundMenu", out var nb)) s.NativeBackgroundMenu = nb.GetBoolean();
                if (doc.RootElement.TryGetProperty("IconSize", out var iz) && iz.ValueKind == JsonValueKind.Number) s.IconSize = iz.GetInt32();
                if (doc.RootElement.TryGetProperty("ShowRecycleBin", out var srb)) s.ShowRecycleBin = srb.GetBoolean();
                if (doc.RootElement.TryGetProperty("SoftwareRender", out var sr)) s.SoftwareRender = sr.GetBoolean();
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(
                new { FreePlacement, MenuBlacklist, MenuInMainProcess, AccentColor, UseStacks, StackGroupBy, DynamicWallpaper, DynamicNoShadows, DynamicNoAnimations, FastAutostart, Language, SpacePreview, NativeBackgroundMenu, IconSize, ShowRecycleBin, SoftwareRender },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
