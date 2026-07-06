using System.IO;

namespace MacDesk.Services;

internal static class Log
{
    private static readonly string _file =
        Path.Combine(AppContext.BaseDirectory, "macdesk.log");
    private static readonly object _lock = new();

    public static void Write(string msg)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(_file, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }
}
