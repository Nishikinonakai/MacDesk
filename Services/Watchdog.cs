using System.Diagnostics;

namespace MacDesk.Services;

/// <summary>
/// 独立看门狗进程（<c>MacDesk.exe --watchdog &lt;mainPid&gt; [modeArgs]</c>）。
///
/// 主进程是挂在 SHELLDLL_DefView 下的 WPF 子窗口；Explorer/shell 重启会销毁那个父窗口，
/// 连带我们的子窗口——实测主进程会**突然死亡**（OnClosed、Dispatcher 计时器、托管异常
/// 兜底全都来不及跑，也不产生 WER 崩溃报告）。所以恢复不能靠主进程自救。
///
/// 这个无窗口的兄弟进程不挂靠 shell，能扛过 Explorer 重启：它盯着主进程，主进程一旦非
/// 正常消失（崩溃 / 被 shell 带走 / 分辨率变化主动退出）就重新拉起一个（新实例走"启动即
/// 挂载 + 等 DefView 出现"的可靠路径）。用户主动退出时主进程置 CleanQuit 事件，看门狗随
/// 之退出、不再拉起。带指数退避，防止主进程反复秒退时热循环。
/// </summary>
internal static class Watchdog
{
    private const string WatchdogMutexName = "MacDesk.Watchdog";
    private const string CleanQuitEventName = "MacDesk.CleanQuit";

    /// <summary>主进程启动时调用：若还没有看门狗，拉起一个盯着自己。
    /// 返回 true = 本次真的拉起了新看门狗；false = 已有现役（交接接管方靠此轮询等
    /// 老看门狗退场后再武装自己）。</summary>
    public static bool EnsureRunning(IEnumerable<string> modeArgs)
    {
        try
        {
            try { using var _ = Mutex.OpenExisting(WatchdogMutexName); return false; } // 已有看门狗
            catch (WaitHandleCannotBeOpenedException) { /* 没有，往下拉起 */ }

            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (var a in modeArgs) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Log.Write($"watchdog spawned for main pid={Environment.ProcessId}");
            return true;
        }
        catch (Exception ex) { Log.Write("EnsureRunning failed: " + ex.Message); return false; }
    }

    /// <summary>用户主动退出：置 CleanQuit，让看门狗停手（而非把主进程再拉起来）。</summary>
    public static void SignalCleanQuit()
    {
        try { using var e = EventWaitHandle.OpenExisting(CleanQuitEventName); e.Set(); }
        catch { /* 没有看门狗在跑 */ }
    }

    /// <summary>看门狗主循环（阻塞直到清洁退出）。</summary>
    public static void Run(int initialPid, IEnumerable<string> modeArgs)
    {
        using var wd = new Mutex(true, WatchdogMutexName, out bool onlyOne);
        if (!onlyOne) return; // 已有看门狗，退让
        using var cleanQuit = new EventWaitHandle(false, EventResetMode.ManualReset, CleanQuitEventName);
        var mode = modeArgs.ToArray();
        Log.Write($"watchdog running, tracking pid={initialPid} mode=[{string.Join(" ", mode)}]");

        int pid = initialPid;
        int lastLaunchTick = 0; // 0 = 初始主进程由用户启动，非看门狗拉起
        int failStreak = 0;

        while (true)
        {
            // 盯着当前主进程，直到它消失或用户清洁退出
            Process? proc = null;
            try { proc = Process.GetProcessById(pid); } catch { }
            while (proc is { HasExited: false })
            {
                if (cleanQuit.WaitOne(0)) { cleanQuit.Reset(); Log.Write("watchdog: clean quit"); return; }
                proc.WaitForExit(200); // ≤200ms 感知主进程退出，重启接管几乎无闪屏
            }
            if (cleanQuit.WaitOne(0)) { cleanQuit.Reset(); Log.Write("watchdog: clean quit"); return; }

            // 主进程非正常消失 → 退避后重新拉起
            int aliveMs = lastLaunchTick == 0 ? int.MaxValue : Environment.TickCount - lastLaunchTick;
            failStreak = aliveMs < 5000 ? failStreak + 1 : 0;
            if (failStreak > 0)
            {
                int backoff = Math.Min(failStreak * 2000, 20000);
                Log.Write($"watchdog: main died fast (streak={failStreak}), backoff {backoff}ms");
                if (cleanQuit.WaitOne(backoff)) { cleanQuit.Reset(); return; }
            }

            pid = LaunchMain(mode);
            lastLaunchTick = Environment.TickCount;
            if (pid == 0 && cleanQuit.WaitOne(2000)) { cleanQuit.Reset(); return; }
        }
    }

    private static int LaunchMain(string[] modeArgs)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in modeArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--recovered"); // 主进程据此知道挂载失败要安静重试、别弹框
            var p = Process.Start(psi);
            Log.Write($"watchdog relaunched main pid={p?.Id}");
            return p?.Id ?? 0;
        }
        catch (Exception ex) { Log.Write("watchdog relaunch failed: " + ex.Message); return 0; }
    }
}
