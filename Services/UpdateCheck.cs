using System.Net.Http;
using System.Text.Json;

namespace MacDesk.Services;

/// <summary>
/// 检查更新（About 页手动触发）。来源 = GitHub Releases 最新 tag。
/// 全软件唯一的联网点；无后端、无遥测，不检查就不联网。
/// </summary>
internal static class UpdateCheck
{
    private const string Api = "https://api.github.com/repos/Nishikinonakai/MacDesk/releases/latest";
    public const string ReleasesPage = "https://github.com/Nishikinonakai/MacDesk/releases";

    public static string CurrentVersion =>
        typeof(UpdateCheck).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>返回（是否有新版, 展示文案, 可打开的发布页 url 或 null）。</summary>
    public static async Task<(bool HasUpdate, string Message, string? Url)> Run()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MacDesk-UpdateCheck");
            var json = await http.GetStringAsync(Api);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            var latest = ParseVersion(tag);
            var mine = ParseVersion(CurrentVersion);
            if (latest > mine) return (true, $"发现新版本 {tag}（当前 {CurrentVersion}）", url);
            return (false, $"已是最新版本（{CurrentVersion}）", null);
        }
        catch (HttpRequestException ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return (false, "无法访问 GitHub 发布页（网络问题或仓库未公开）", null);
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return (false, "检查更新失败", null);
        }
    }

    private static Version ParseVersion(string s)
    {
        s = s.TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }
}
