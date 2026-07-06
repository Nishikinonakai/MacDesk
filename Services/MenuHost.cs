using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using MacDesk.Interop;

namespace MacDesk.Services;

/// <summary>
/// 常驻右键菜单子进程（<c>MacDesk.exe --menuhost &lt;mainPid&gt;</c>）。
///
/// 旧方案每次右键冷启动一个一次性子进程（几百 ms 才见菜单）。现在主进程启动时预热一个
/// 常驻 host：命名管道收请求 → 立刻 TrackPopupMenu。第三方 shell 扩展的 DLL 在预热时
/// 就加载好，后续右键零冷启动。隔离性不变：host 被崩溃扩展带走 → 下次请求自动重拉。
///
/// 协议（每请求一次连接，US=0x1F 分隔）：verb US x US y US path[ US path...]
///   verb = "files"（文件项菜单）| "bg"（桌面背景菜单）
/// 菜单收起：命名事件 MacDesk.Cmd.DismissMenu → host 给 owner 发 WM_CANCELMODE。
/// </summary>
internal static class MenuHost
{
    private const string PipeName = "MacDesk.MenuHost";
    private const char US = '\x1F';

    // ── 客户端（主进程侧） ────────────────────────────────────

    private static Process? _host;
    private static readonly object _lock = new();

    /// <summary>主进程启动时预热 host（shell 扩展 DLL 加载几百 ms，别等第一次右键才付）。</summary>
    public static void EnsureSpawned()
    {
        lock (_lock)
        {
            if (_host is { HasExited: false }) return;
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                psi.ArgumentList.Add("--menuhost");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                _host = Process.Start(psi);
                Log.Write($"menu host spawned pid={_host?.Id}");
            }
            catch (Exception ex) { Log.Write("menu host spawn failed: " + ex.Message); }
        }
    }

    public static void RequestFileMenu(int x, int y, string[] paths) => Request("files", x, y, paths);
    public static void RequestBackgroundMenu(int x, int y, string folder) => Request("bg", x, y, new[] { folder });

    /// <summary>收起当前打开的菜单（点击别处/开新菜单前调用）。</summary>
    public static void Dismiss() => CommandChannel.Signal("DismissMenu");

    /// <summary>请求走后台线程：host 死了要重拉时（最长几秒）别卡 UI 线程。</summary>
    private static void Request(string verb, int x, int y, string[] paths) =>
        Task.Run(() => RequestCore(verb, x, y, paths));

    private static void RequestCore(string verb, int x, int y, string[] paths)
    {
        // 注意：这里不能先 Dismiss()——信号线程可能晚于管道请求被调度，把自己刚开的菜单
        // 秒杀（真机竞态实测）。旧菜单开着时新右键会被 OS 当"点击外部"自动关掉（菜单有
        // 前台权限），host 串行循环随后自然接到本请求。
        var payload = $"{verb}{US}{x}{US}{y}{US}{string.Join(US, paths)}";
        if (TrySend(payload, 800)) return;
        Log.Write("menu host unreachable, respawning");
        lock (_lock) { try { _host?.Kill(); } catch { } _host = null; }
        EnsureSpawned();
        if (TrySend(payload, 3000)) return;
        // 双保险：退化为旧的一次性子进程
        Log.Write("menu host still unreachable, falling back to one-shot helper");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add(verb == "bg" ? "--bgmenu" : "--contextmenu");
            psi.ArgumentList.Add(x.ToString());
            psi.ArgumentList.Add(y.ToString());
            foreach (var p in paths) psi.ArgumentList.Add(p);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Write("one-shot menu fallback failed: " + ex.Message); }
    }

    private static bool TrySend(string payload, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(payload);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch { return false; }
    }

    // ── 服务端（host 进程侧，专用 STA 线程阻塞运行） ──────────

    private static IntPtr _ownerHwnd;
    private static volatile bool _menuOpen;

    public static void Run(int parentPid)
    {
        // 主进程退出（含被 shell 带走）→ host 跟着退，别留孤儿
        new Thread(() =>
        {
            try { Process.GetProcessById(parentPid).WaitForExit(); } catch { }
            Environment.Exit(0);
        }) { IsBackground = true }.Start();

        // 深色菜单（uxtheme #135/#136）暂未启用：曾疑似白块菜单元凶，后证实白块真凶是
        // TPM 淡入动画（见 TPM_NOANIMATION）。需要深色跟随时可再试 EnableModernMenuTheme()。
        var owner = new MessageWindow(registerHotkey: false);
        _ownerHwnd = owner.Handle;

        // 点别处收菜单：主进程发命名事件，我们给 owner 发 WM_CANCELMODE 结束模态菜单循环。
        // 只在菜单真开着时才 post——线程在两次菜单之间不泵消息，陈旧的 WM_CANCELMODE 会
        // 滞留队列、把下一个菜单秒杀（真机踩坑：菜单永远不显示）。
        CommandChannel.Listen("DismissMenu", () =>
        {
            if (_menuOpen && _ownerHwnd != IntPtr.Zero)
                Native.PostMessage(_ownerHwnd, Native.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
        });

        // 预热：背景菜单扩展在本进程加载（实测安全）；文件项扩展会 fail-fast 带走进程，
        // 只能用牺牲进程探针（ProbeSafe），顺带把常见类型的判定缓存好。
        try
        {
            ShellContextMenu.Prewarm(DesktopItemProvider.UserDesktop, _ownerHwnd);
            Log.Write("menu host prewarmed");
        }
        catch (Exception ex) { Log.Write("menu host prewarm failed: " + ex.Message); }
        try
        {
            var sample = Directory.EnumerateFileSystemEntries(DesktopItemProvider.UserDesktop).FirstOrDefault();
            if (sample != null) ProbeSafe(sample);
        }
        catch { }

        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                server.WaitForConnection();
                string payload;
                using (var r = new StreamReader(server, Encoding.UTF8)) payload = r.ReadToEnd();
                var parts = payload.Split(US);
                if (parts.Length < 4) continue;
                int x = int.Parse(parts[1]), y = int.Parse(parts[2]);
                var paths = parts.Skip(3).ToArray();
                Log.Write($"menu host: {parts[0]} request at {x},{y} ({paths.Length} path(s))");
                bool full = parts[0] != "bg" && ProbeSafe(paths[0]);
                _menuOpen = true;
                try
                {
                    if (parts[0] == "bg")
                        ShellContextMenu.ShowBackground(paths[0], _ownerHwnd, x, y);
                    else if (full)
                        ShellContextMenu.Show(paths, _ownerHwnd, x, y);
                    else
                        ShellContextMenu.ShowDegraded(paths, _ownerHwnd, x, y);
                }
                finally { _menuOpen = false; }
            }
            catch (Exception ex) { Log.Write("menu host loop error: " + ex.Message); }
        }
    }

    // ── 文件菜单安全性探针（结果按文件类型缓存） ──────────────

    private static readonly Dictionary<string, bool> _safeByKind = new();

    private static string KindKey(string path)
    {
        try { if (Directory.Exists(path)) return "<dir>"; } catch { }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext.Length == 0 ? "<none>" : ext;
    }

    /// <summary>该路径的原生文件菜单是否能安全加载（牺牲进程实测，非 0 退出码/崩溃 = 不安全）。</summary>
    private static bool ProbeSafe(string path)
    {
        string key = KindKey(path);
        if (_safeByKind.TryGetValue(key, out bool cached)) return cached;
        bool safe = false;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--menuprobe");
            psi.ArgumentList.Add(path);
            var p = Process.Start(psi)!;
            safe = p.WaitForExit(8000) && p.ExitCode == 0;
            if (!p.HasExited) { try { p.Kill(); } catch { } }
        }
        catch (Exception ex) { Log.Write("probe spawn failed: " + ex.Message); }
        _safeByKind[key] = safe;
        Log.Write($"menu probe [{key}] -> {(safe ? "full native" : "degraded")}");
        return safe;
    }
}
