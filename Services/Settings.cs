using System.IO;
using System.Text.Json;

namespace MacDesk.Services;

/// <summary>轻量用户设置（%LOCALAPPDATA%\MacDesk\settings.json）。目前只有自由摆放开关。</summary>
internal sealed class Settings
{
    private readonly string _file;

    /// <summary>自由摆放：拖放落点即位置、重排不吸附网格（macOS arrangeBy=none）。默认关（Windows 习惯网格）。</summary>
    public bool FreePlacement { get; set; }

    /// <summary>右键菜单黑名单：菜单项文本含任一子串（不分大小写）即被移除。
    /// 手工编辑 settings.json 配置；设置 GUI 做出来之前的过渡形态（机主点名讨厌 AMD 项）。</summary>
    public List<string> MenuBlacklist { get; set; } = new() { "AMD Software" };

    /// <summary>菜单序列化进主进程同线程弹出（前台战争终极解）。false = 回退旧 host 内
    /// TrackPopupMenu 路径（settle-wait+重试），新路径出问题时的免重建逃生口。</summary>
    public bool MenuInMainProcess { get; set; } = true;

    /// <summary>强调色 key（见 Accent.Palette），影响选中标签/框选颜色。</summary>
    public string AccentColor { get; set; } = "blue";

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
                new { FreePlacement, MenuBlacklist, MenuInMainProcess, AccentColor },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
