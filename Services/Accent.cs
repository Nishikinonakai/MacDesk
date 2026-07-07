using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace MacDesk.Services;

/// <summary>
/// 强调色（macOS 外观设置同款）：更改后选中标签底色/框选颜色即时切换。
/// 调色板取 macOS 浅色模式的强调色系；"蓝色"保持项目一直在用的 mac 选中蓝。
/// </summary>
internal static class Accent
{
    public static readonly (string Key, string Name, Color Color)[] Palette =
    {
        ("blue",     "蓝色", Color.FromRgb(0x2B, 0x63, 0xD9)),
        ("purple",   "紫色", Color.FromRgb(0x95, 0x3D, 0x96)),
        ("pink",     "粉色", Color.FromRgb(0xF7, 0x4F, 0x9E)),
        ("red",      "红色", Color.FromRgb(0xE0, 0x38, 0x3E)),
        ("orange",   "橙色", Color.FromRgb(0xF7, 0x82, 0x1B)),
        ("yellow",   "黄色", Color.FromRgb(0xE6, 0xB3, 0x00)),
        ("green",    "绿色", Color.FromRgb(0x62, 0xBA, 0x46)),
        ("graphite", "石墨", Color.FromRgb(0x79, 0x79, 0x79)),
    };

    public static event Action? Changed;

    public static Color Current
    {
        get
        {
            var key = MacDesk.Desktop.Config?.AccentColor ?? "blue";
            foreach (var p in Palette)
                if (p.Key == key) return p.Color;
            return Palette[0].Color;
        }
    }

    public static void Set(string key)
    {
        MacDesk.Desktop.Config.AccentColor = key;
        MacDesk.Desktop.Config.Save();
        _label = _band = _bandStroke = null;
        Changed?.Invoke();
    }

    private static SolidColorBrush? _label, _band, _bandStroke;

    /// <summary>选中标签底色（0xE6 透明度的强调色）。</summary>
    public static SolidColorBrush LabelBrush =>
        _label ??= Frozen(Color.FromArgb(0xE6, Current.R, Current.G, Current.B));

    /// <summary>框选填充。</summary>
    public static SolidColorBrush BandFill =>
        _band ??= Frozen(Color.FromArgb(0x30, Current.R, Current.G, Current.B));

    /// <summary>框选描边。</summary>
    public static SolidColorBrush BandStroke =>
        _bandStroke ??= Frozen(Color.FromArgb(0x90, Current.R, Current.G, Current.B));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
