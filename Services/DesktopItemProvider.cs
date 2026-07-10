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

    // ── 原生桌面虚拟项的 shell 解析名（"桌面图标设置"那一组，布局按此字符串为键）──
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    public const string ThisPC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public const string UserFiles = "::{59031A47-3F72-44A7-89C5-5595FE6B30EE}";
    public const string Network = "::{F02C1A0D-3E12-4590-B168-3C813C6A0D06}";
    public const string ControlPanel = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";

    public IReadOnlyList<DesktopEntry> Enumerate()
    {
        // 虚拟项按设置开关出镜（默认只有回收站，与系统缺省一致）。显示名跟应用语言；
        // "用户文件"图标 Windows 原生就以账户名显示，照做
        var cfg = MacDesk.Desktop.Config;
        var result = new List<DesktopEntry>();
        if (cfg.ShowRecycleBin) result.Add(new(RecycleBin, L.T("回收站", "Recycle Bin")));
        if (cfg.ShowThisPC) result.Add(new(ThisPC, L.T("此电脑", "This PC")));
        if (cfg.ShowUserFiles) result.Add(new(UserFiles, Environment.UserName));
        if (cfg.ShowNetwork) result.Add(new(Network, L.T("网络", "Network")));
        if (cfg.ShowControlPanel) result.Add(new(ControlPanel, L.T("控制面板", "Control Panel")));
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
