using System.IO;

namespace MacDesk.Services;

public sealed record DesktopEntry(string Path, string DisplayName);

/// <summary>合并用户桌面与公共桌面，FileSystemWatcher 监听变化。</summary>
internal sealed class DesktopItemProvider : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();

    public event Action? Changed;

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public DesktopItemProvider()
    {
        foreach (var dir in new[] { UserDesktop, PublicDesktop })
        {
            if (!Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            w.Created += (_, _) => Changed?.Invoke();
            w.Deleted += (_, _) => Changed?.Invoke();
            w.Renamed += (_, _) => Changed?.Invoke();
            _watchers.Add(w);
        }
    }

    /// <summary>回收站的 shell 解析名（虚拟项，不是文件）</summary>
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    public IReadOnlyList<DesktopEntry> Enumerate()
    {
        var result = new List<DesktopEntry>();
        if (Desktop.Config.ShowRecycleBin)
            result.Add(new DesktopEntry(RecycleBin, "Recycle Bin"));
        foreach (var dir in new[] { UserDesktop, PublicDesktop })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(path);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.Hidden)) continue;
                // 显示名：快捷方式去掉 .lnk / .url 扩展名
                var display = name;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is ".lnk" or ".url") display = Path.GetFileNameWithoutExtension(name);
                result.Add(new DesktopEntry(path, display));
            }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}
