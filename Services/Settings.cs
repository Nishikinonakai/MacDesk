using System.IO;
using System.Text.Json;

namespace MacDesk.Services;

/// <summary>轻量用户设置（%LOCALAPPDATA%\MacDesk\settings.json）。目前只有自由摆放开关。</summary>
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
                new { FreePlacement, MenuBlacklist, MenuInMainProcess, AccentColor, UseStacks, StackGroupBy, DynamicWallpaper, DynamicNoShadows, DynamicNoAnimations, FastAutostart },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
