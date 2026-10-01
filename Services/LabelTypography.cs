using FontFamily = System.Windows.Media.FontFamily;

namespace MacDesk.Services;

internal static class LabelTypography
{
    public const string BundledFontName = "Sarasa UI SC";
    private static readonly FontFamily BundledFont = new(
        new Uri("pack://application:,,,/MacDesk;component/"),
        "./Assets/Fonts/#Sarasa UI SC");

    public static FontFamily Resolve(string? name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(name, BundledFontName, StringComparison.OrdinalIgnoreCase)
            ? BundledFont : new FontFamily(name);
}
