using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace MacDesk.Services;

/// <summary>
/// 开机自启，两种机制（注册值都保留启动时的模式开关，如 --hide-native）：
///  - Run 键（默认）：HKCU\...\Run，标准、在任务管理器"启动应用"里可见，但受 Windows
///    对启动项的人为串行延迟（机主实测登录后 ~40s+ 才起）。
///  - 计划任务（加速）：onlogon 触发即启、无启动延迟，Priority 5（默认 7 = below normal
///    会拖慢首屏挂载）。非管理员注册自己名下的登录任务是允许的；失败自动回退 Run 键。
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MacDesk";
    private const string TaskName = "MacDesk";

    public static bool IsEnabled() => RunEntryExists() || TaskExists();

    /// <summary>当前是否计划任务（加速）机制在生效。</summary>
    public static bool IsFastMode() => TaskExists();

    public static void Enable(IEnumerable<string> modeArgs, bool fast = false)
    {
        // --soft 不烙进自启：软件渲染的持久真相源是 settings.json 的 SoftwareRender（GUI 开关），
        // --soft 只是会话级实验开关（会话内的看门狗/交接重启走 LaunchModeArgs 仍保留）。
        // 否则从 --soft 会话里开自启会把它写死进 Run 键，之后 GUI 关开关"关不掉"。
        var args = modeArgs.Where(a => a != "--soft").ToArray();
        if (fast && EnableTask(args))
        {
            DeleteRunEntry(); // 双机制并存会双启（单实例互斥体兜得住，但没必要）
            Log.Write("autostart enabled (scheduled task, fast)");
            return;
        }
        if (fast) Log.Write("autostart: task registration failed, falling back to Run key");
        DeleteTask();
        WriteRunEntry(args);
    }

    public static void Disable()
    {
        DeleteRunEntry();
        DeleteTask();
        Log.Write("autostart disabled");
    }

    public static void Toggle(IEnumerable<string> modeArgs)
    {
        if (IsEnabled()) Disable();
        else Enable(modeArgs, Desktop.Config?.FastAutostart ?? false);
    }

    // ── Run 键机制 ────────────────────────────────────────────

    private static bool RunEntryExists()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private static void WriteRunEntry(string[] modeArgs)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            string exe = Environment.ProcessPath!;
            string tail = string.Join(" ", modeArgs.Select(Quote));
            k?.SetValue(ValueName, $"{Quote(exe)}{(tail.Length > 0 ? " " + tail : "")}");
            Log.Write($"autostart enabled (Run key): {exe} {tail}");
        }
        catch (Exception ex) { Log.Write("autostart enable failed: " + ex.Message); }
    }

    private static void DeleteRunEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ── 计划任务机制 ──────────────────────────────────────────

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"").code == 0;

    private static bool EnableTask(string[] modeArgs)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{Esc(user)}</UserId>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{Esc(user)}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>LeastPrivilege</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>false</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <IdleSettings>
                      <StopOnIdleEnd>false</StopOnIdleEnd>
                      <RestartOnIdle>false</RestartOnIdle>
                    </IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>5</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{Esc(exe)}</Command>
                      <Arguments>{Esc(string.Join(" ", modeArgs.Select(Quote)))}</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;
            string tmp = Path.Combine(Path.GetTempPath(), "macdesk-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode); // schtasks 只认 UTF-16 的 XML
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            try { File.Delete(tmp); } catch { }
            if (code != 0) Log.Write($"schtasks create failed code={code}: {output}");
            return code == 0;
        }
        catch (Exception ex)
        {
            Log.Write("autostart task enable failed: " + ex.Message);
            return false;
        }
    }

    private static void DeleteTask()
    {
        if (TaskExists()) RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
