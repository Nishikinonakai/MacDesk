namespace MacDesk.Services;

/// <summary>
/// 菜单子进程 → 主进程的命令通道（同会话命名事件）。
/// 主进程 Listen 注册处理器；子进程 Signal 触发。
/// </summary>
internal static class CommandChannel
{
    private static string EventName(string cmd) => $"MacDesk.Cmd.{cmd}";

    private static readonly List<(EventWaitHandle Evt, Thread Waiter)> _listeners = new();

    public static void Listen(string cmd, Action handler)
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(cmd));
        var t = new Thread(() =>
        {
            while (true)
            {
                evt.WaitOne();
                try { handler(); } catch { }
            }
        }) { IsBackground = true, Name = $"cmd-{cmd}" };
        t.Start();
        _listeners.Add((evt, t));
    }

    public static void Signal(string cmd)
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(EventName(cmd));
            evt.Set();
        }
        catch { /* 主进程不在 */ }
    }
}
