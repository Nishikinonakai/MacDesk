using System.IO;

namespace MacDesk.Services;

internal static class Log
{
    private static readonly string _file =
        Path.Combine(AppContext.BaseDirectory, "macdesk.log");
    private static readonly object _lock = new();
    private static int _sinceSizeCheck = 127; // 首条写入就检查一次（进程重启点）

    public static string FilePath => _file;

    public static void Write(string msg)
    {
        try
        {
            lock (_lock)
            {
                // 轮转：超 2MB 滚到 .1（保留上一代，共约 4MB 上限），别让日志无限吃盘
                if (++_sinceSizeCheck >= 128)
                {
                    _sinceSizeCheck = 0;
                    var fi = new FileInfo(_file);
                    if (fi.Exists && fi.Length > 2_000_000)
                        File.Move(_file, _file + ".1", overwrite: true);
                }
                File.AppendAllText(_file, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
        }
        catch { }
    }
}
