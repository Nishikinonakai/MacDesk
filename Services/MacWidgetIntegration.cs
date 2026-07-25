using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MacDesk.Services;

/// <summary>
/// MacWidget 的可选联动入口。MacDesk 不依赖 MacWidget 的程序集：只按安装路径/运行进程发现它，
/// 并用会话内命名事件请求打开编辑组件库。因此任一产品未安装、升级错峰或联动协议缺席时，
/// 两边都能独立正常工作。
/// </summary>
internal static class MacWidgetIntegration
{
    public const string EditWidgetsEventName = "MacWidget.Command.EditWidgets.v1";

    internal sealed record Installation(string? ExecutablePath, bool IsRunning, bool IsPrototype)
    {
        public bool Detected => IsRunning || !string.IsNullOrEmpty(ExecutablePath);
    }

    /// <summary>发现正式 MacWidget，也兼容当前测试阶段的 WidgetProto 可执行文件。</summary>
    public static Installation Detect()
    {
        var running = FindRunningExecutable();
        if (running != null)
            return new Installation(running, IsRunning: true,
                IsPrototype: Path.GetFileName(running).Equals("WidgetProto.exe", StringComparison.OrdinalIgnoreCase));

        foreach (var path in CandidateExecutables())
            if (File.Exists(path))
                return new Installation(path, IsRunning: false,
                    IsPrototype: Path.GetFileName(path).Equals("WidgetProto.exe", StringComparison.OrdinalIgnoreCase));

        return new Installation(null, IsRunning: false, IsPrototype: false);
    }

    /// <summary>请求运行中的 MacWidget 拉起编辑态；没运行时从已发现的安装副本启动它。</summary>
    public static bool OpenEditor(out string? failure)
    {
        failure = null;
        if (TrySignalEditor()) return true;

        var install = Detect();
        if (!install.Detected)
        {
            failure = "MacWidget 未安装";
            return false;
        }

        // 已运行却没有事件，通常是尚未升级到支持联动的旧版；绝不能再开第二组桌面组件。
        if (install.IsRunning)
        {
            failure = "运行中的 MacWidget 版本尚不支持从 MacDesk 打开组件库";
            return false;
        }

        if (string.IsNullOrEmpty(install.ExecutablePath))
        {
            failure = "未找到 MacWidget 可执行文件";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(install.ExecutablePath, "--edit-widgets") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static bool TrySignalEditor()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(EditWidgetsEventName);
            return evt.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? FindRunningExecutable()
    {
        foreach (var name in new[] { "MacWidget", "WidgetProto" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { /* 某些跨权限进程不允许读取 MainModule，继续找其余候选。 */ }
                finally { process.Dispose(); }
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateExecutables()
    {
        // App Paths 是正式安装器的首选登记处；另列出常见单用户/开发部署位置，保证当前原型也可联调。
        foreach (var exe in new[] { "MacWidget.exe", "WidgetProto.exe" })
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                string? path = null;
                try
                {
                    using var key = hive.OpenSubKey(@$"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                    path = key?.GetValue(null) as string;
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var path in new[]
        {
            Path.Combine(local, "Programs", "MacWidget", "MacWidget.exe"),
            Path.Combine(local, "Programs", "MacWidget", "WidgetProto.exe"),
            Path.Combine(programFiles, "MacWidget", "MacWidget.exe"),
            Path.Combine(@"C:\\work\\widgetproto\\app", "MacWidget.exe"),
            Path.Combine(@"C:\\work\\widgetproto\\app", "WidgetProto.exe"),
        }) yield return path;
    }
}
