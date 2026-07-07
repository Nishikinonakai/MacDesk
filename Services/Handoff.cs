using System.IO;
using System.Text.Json;

namespace MacDesk.Services;

/// <summary>
/// 分辨率变化的原地平滑交接（v2 方案，取代"自杀等看门狗拉起"的 ~1s 裸桌面闪屏）。
///
/// 流程：老进程收到显示变化 → 写位置种子 + 释放热键/单实例互斥体 → 直接 spawn
/// `--handoff &lt;oldPid&gt;` 替身 → 老进程窗口原地撑住画面；替身走"启动时挂载"的可靠
/// 路径在新分辨率下就绪（读种子把图标先放到按比例映射的旧位置，再动画滑向推导位 =
/// macOS 式 morph），全部窗口挂载完置 Ready 事件 → 老进程这才退休（CleanQuit 老看门狗，
/// 替身随后拉起自己的看门狗）。替身超时未就绪 → 老进程按旧路径退出，看门狗兜底。
///
/// 为什么不活体改尺寸：WPF 子窗口在分辨率变化后布局账本卡死（多轮实测定案，见 dev-notes）。
/// </summary>
internal static class Handoff
{
    public const string ReadyEventName = "MacDesk.HandoffReady";

    private static string SeedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDesk", "handoff-seed.json");

    public sealed class MonitorSeed
    {
        public double W { get; set; }
        public double H { get; set; }
        public Dictionary<string, double[]> Icons { get; set; } = new();
    }

    /// <summary>老进程侧：把每窗口的图标当前 DIU 坐标 + 工作区尺寸落盘。</summary>
    public static void WriteSeed(Dictionary<string, MonitorSeed> monitors)
    {
        try
        {
            File.WriteAllText(SeedPath, JsonSerializer.Serialize(monitors));
            Log.Write($"handoff seed written ({monitors.Count} monitor(s))");
        }
        catch (Exception ex) { Log.Write("handoff seed write failed: " + ex.Message); }
    }

    /// <summary>替身侧：读种子（30s 内的才算数，读完即删——种子只属于这一次交接）。</summary>
    public static Dictionary<string, MonitorSeed>? TryLoadSeed()
    {
        try
        {
            if (!File.Exists(SeedPath)) return null;
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(SeedPath)).TotalSeconds > 30)
            {
                File.Delete(SeedPath);
                return null;
            }
            var seed = JsonSerializer.Deserialize<Dictionary<string, MonitorSeed>>(File.ReadAllText(SeedPath));
            File.Delete(SeedPath);
            return seed;
        }
        catch { return null; }
    }

    /// <summary>替身侧：全部窗口挂载+首排完成，通知老进程可以退休了。</summary>
    public static void SignalReady()
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(ReadyEventName);
            e.Set();
            Log.Write("handoff ready signaled");
        }
        catch (Exception ex) { Log.Write("handoff ready signal failed (old gone?): " + ex.Message); }
    }
}
