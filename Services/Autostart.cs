using Microsoft.Win32;

namespace MacDesk.Services;

/// <summary>
/// 开机自启：写 HKCU\...\Run（登录即启，无需管理员）。
/// 注册值保留启动时的模式开关（如 --hide-native），自启复现用户选的模式。
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MacDesk";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void Enable(IEnumerable<string> modeArgs)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            string exe = Environment.ProcessPath!;
            string tail = string.Join(" ", modeArgs.Select(Quote));
            k?.SetValue(ValueName, $"{Quote(exe)}{(tail.Length > 0 ? " " + tail : "")}");
            Log.Write($"autostart enabled: {exe} {tail}");
        }
        catch (Exception ex) { Log.Write("autostart enable failed: " + ex.Message); }
    }

    public static void Disable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Write("autostart disabled");
        }
        catch (Exception ex) { Log.Write("autostart disable failed: " + ex.Message); }
    }

    public static void Toggle(IEnumerable<string> modeArgs)
    {
        if (IsEnabled()) Disable();
        else Enable(modeArgs);
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
