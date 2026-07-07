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

    /// <summary>返回（是否有新版, 展示文案, 可打开的发布页 url 或 null）。
    /// 主通道 = releases/latest 的 HTML 302 重定向（Location 头直接带 tag，github.com
    /// 页面端点无 API 限额）；API 只做备用——匿名 REST API 在共享代理出口 IP 下常年
    /// 403 限流（真机实测：v1.0.0 发版当天 API 403、重定向通道正常）。</summary>
    public static async Task<(bool HasUpdate, string Message, string? Url)> Run()
    {
        try
        {
            string? tag = await LatestTagViaRedirect() ?? await LatestTagViaApi();
            if (tag == null)
                return (false, "无法访问 GitHub（网络问题或接口限流），请稍后再试", null);
            var latest = ParseVersion(tag);
            var mine = ParseVersion(CurrentVersion);
            if (latest > mine) return (true, $"发现新版本 {tag}（当前 {CurrentVersion}）", $"{ReleasesPage}/tag/{tag}");
            return (false, $"已是最新版本（{CurrentVersion}）", null);
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return (false, "检查更新失败", null);
        }
    }

    private static async Task<string?> LatestTagViaRedirect()
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MacDesk-UpdateCheck");
            using var resp = await http.GetAsync(ReleasesPage + "/latest");
            if (resp.Headers.Location is not { } loc) return null;
            var s = Uri.UnescapeDataString(loc.ToString().TrimEnd('/'));
            var tail = s[(s.LastIndexOf('/') + 1)..];
            // 无任何 release 时重定向回 /releases 本身
            return tail is "releases" or "latest" or "" ? null : tail;
        }
        catch (Exception ex)
        {
            Log.Write("update check (redirect) failed: " + ex.Message);
            return null;
        }
    }

    private static async Task<string?> LatestTagViaApi()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MacDesk-UpdateCheck");
            var json = await http.GetStringAsync(Api);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch (Exception ex)
        {
            Log.Write("update check (api) failed: " + ex.Message);
            return null;
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
