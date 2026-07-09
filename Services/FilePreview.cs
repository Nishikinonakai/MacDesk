using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MacDesk.Services;

/// <summary>
/// 空格键文件预览：兼容第三方"选中文件按空格预览"插件。选中项按下空格时，尝试驱动
/// 已安装的预览器；没装任何一个就静默 no-op（机主的原话："没装那就是没装"）。
///
/// 探测优先级 QuickLook &gt; Seer &gt; Peek，取首个可用者：
///  - QuickLook：命名管道 \\.\pipe\QuickLook.App.Pipe.&lt;SID&gt;，写一行 UTF-8(无 BOM)
///    "QuickLook.App.PipeMessages.Toggle|&lt;完整路径&gt;"（协议对齐 QL-Win 官方 server 与 Files 的 client）。
///  - Seer：FindWindow("SeerWindowClass") + WM_COPYDATA(dwData=5000, Unicode 路径)。
///  - PowerToys Peek（v0.95+）：启动 PowerToys.Peek.UI.exe "&lt;路径&gt;"（Peek 无 IPC，只有 CLI）。
///
/// 我们不模拟"在 Explorer 里按空格"（预览器的全局钩子不认识我们的窗口，也读不到原生
/// ListView 的选中集）——改走各预览器给第三方文件管理器的官方外部激活接口。
/// </summary>
internal static class FilePreview
{
    private enum Provider { None, QuickLook, Seer, Peek }

    // 上一次 Toggle 用的是谁：方向键跟随（Switch）只发给它，避免探测多个。
    private static Provider _last = Provider.None;

    /// <summary>按空格：切换焦点文件的预览。返回 true = 有预览器接手（据此翻转"是否已打开"）。</summary>
    public static bool Toggle(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (QuickLookAvailable()) { _last = Provider.QuickLook; QuickLookSend(QlToggle, path); return true; }
        if (SeerWindow() != IntPtr.Zero) { _last = Provider.Seer; SeerSend(path); return true; }
        var peek = PeekExe();
        if (peek != null) { _last = Provider.Peek; PeekLaunch(peek, path); return true; }

        _last = Provider.None;
        return false;
    }

    /// <summary>方向键换选中项：预览已打开时让它跟随新焦点。QuickLook/Seer 支持真正的 switch
    /// （不会凭空开窗）；Peek 无 switch 概念，跟随留白（不重开，免得每次方向键都弹一个 Peek）。
    /// 直接发给上次的 provider、不再重探测：发送本身对已关的预览器是安全 no-op（QuickLook 走
    /// 后台异步 Connect、Seer 走后台 Task），避免每次方向键在 UI 线程枚举命名管道。</summary>
    public static void Switch(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        switch (_last)
        {
            case Provider.QuickLook: QuickLookSend(QlSwitch, path); break;
            case Provider.Seer: SeerSend(path); break;
        }
    }

    // ── QuickLook（命名管道）──────────────────────────────────────────
    private const string QlToggle = "QuickLook.App.PipeMessages.Toggle";
    private const string QlSwitch = "QuickLook.App.PipeMessages.Switch";
    private static string QlPipe => "QuickLook.App.Pipe." + (WindowsIdentity.GetCurrent().User?.Value ?? "");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string name, int timeoutMs);

    /// <summary>探测 QuickLook 是否在跑：WaitNamedPipe 存在即近瞬返回 true、不存在立即 false，
    /// 比 500ms 的 Connect 探测快得多，没装时能立刻放行给 Seer/Peek。
    /// **别用 Directory.EnumerateFiles(@"\\.\pipe\")**：.NET Core/10 上对 \\.\pipe\ 设备路径抛异常
    /// （真机实测——QuickLook 装了也检测不到，误走 Peek；.NET Framework 却能枚举，坑在这）。</summary>
    private static bool QuickLookAvailable()
    {
        try { return WaitNamedPipe(@"\\.\pipe\" + QlPipe, 20); }
        catch { return false; }
    }

    private static void QuickLookSend(string message, string path) => _ = QuickLookSendAsync(message, path);

    private static async Task QuickLookSendAsync(string message, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", QlPipe, PipeDirection.Out);
            await client.ConnectAsync(500);
            using var writer = new StreamWriter(client); // UTF-8 无 BOM，NewLine "\r\n"（对齐 server 的 ReadLine）
            await writer.WriteLineAsync($"{message}|{path}");
            await writer.FlushAsync();
        }
        catch { /* 没装/没跑/管道刚关 => no-op，正是要的行为 */ }
    }

    // ── Seer（WM_COPYDATA）────────────────────────────────────────────
    private const int WM_COPYDATA = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    private static IntPtr SeerWindow()
    {
        var h = FindWindow("SeerWindowClass", null);
        return h == new IntPtr(-1) ? IntPtr.Zero : h;
    }

    /// <summary>SendMessage(WM_COPYDATA) 是同步阻塞的跨进程调用；丢到后台线程，别拿 Seer 卡住 UI。
    /// HGlobal 内存必须活到 SendMessage 返回，所以在同一 lambda 里 alloc/free。</summary>
    private static void SeerSend(string path) => _ = Task.Run(() =>
    {
        var h = SeerWindow();
        if (h == IntPtr.Zero) return;
        IntPtr p = Marshal.StringToHGlobalUni(path);
        try
        {
            var cds = new COPYDATASTRUCT { dwData = (IntPtr)5000, cbData = (path.Length + 1) * 2, lpData = p };
            SendMessage(h, WM_COPYDATA, IntPtr.Zero, ref cds);
        }
        catch { }
        finally { Marshal.FreeHGlobal(p); }
    });

    // ── PowerToys Peek（v0.95+ CLI）──────────────────────────────────
    private static string? _peekExe;
    private static bool _peekSearched;

    /// <summary>Peek 的 exe 路径整会话记忆化（注册表遍历不便宜，别每次空格都走）。
    /// 代价：会话中途新装 Peek 要重启 MacDesk 才认——可接受。</summary>
    private static string? PeekExe()
    {
        if (_peekSearched) return _peekExe;
        _peekExe = FindPeekExe();
        _peekSearched = true;
        return _peekExe;
    }

    /// <summary>沿 Files 的做法：Uninstall 注册表找 PowerToys 的 InstallLocation →
    /// WinUI3Apps\PowerToys.Peek.UI.exe；找不到再兜底默认安装路径。返回 null = 没装（或 &lt;0.95 无 CLI）。</summary>
    private static string? FindPeekExe()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key == null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(subName);
                    if (k?.GetValue("DisplayName") is not string name ||
                        !name.Contains("PowerToys", StringComparison.OrdinalIgnoreCase)) continue;
                    if (k.GetValue("InstallLocation") is not string loc || string.IsNullOrEmpty(loc)) continue;
                    var exe = Path.Combine(loc, "WinUI3Apps", "PowerToys.Peek.UI.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
        }
        foreach (var b in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe"),
            @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe",
        })
            if (File.Exists(b)) return b;
        return null;
    }

    private static void PeekLaunch(string exe, string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }
}
