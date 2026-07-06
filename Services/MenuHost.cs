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

    public static void RequestFileMenu(int x, int y, string[] paths, IntPtr desktopHwnd) =>
        Request("files", x, y, desktopHwnd, paths);

    public static void RequestBackgroundMenu(int x, int y, string folder, IntPtr desktopHwnd) =>
        Request("bg", x, y, desktopHwnd, new[] { folder });

    /// <summary>收起当前打开的菜单（点击别处/开新菜单前调用）。</summary>
    public static void Dismiss() => CommandChannel.Signal("DismissMenu");

    /// <summary>请求走后台线程：host 死了要重拉时（最长几秒）别卡 UI 线程。</summary>
    private static void Request(string verb, int x, int y, IntPtr desktopHwnd, string[] paths) =>
        Task.Run(() => RequestCore(verb, x, y, desktopHwnd, paths));

    private static void RequestCore(string verb, int x, int y, IntPtr desktopHwnd, string[] paths)
    {
        // 注意：这里不能先 Dismiss()——信号线程可能晚于管道请求被调度，把自己刚开的菜单
        // 秒杀（真机竞态实测）。旧菜单开着时新右键会被 OS 当"点击外部"自动关掉（菜单有
        // 前台权限），host 串行循环随后自然接到本请求。
        var payload = $"{verb}{US}{x}{US}{y}{US}{(long)desktopHwnd}{US}{string.Join(US, paths)}";
        if (TrySend(payload, 1500)) return;

        // 连不上 ≠ host 死了——它可能只是忙（探针进程/上一个菜单占着串行循环）。
        // 只有进程真没了才重拉；活着就多等，别把正在干活的 host 杀了（旧版"右键时灵时不灵"元凶）。
        bool alive;
        lock (_lock) alive = _host is { HasExited: false };
        if (alive && TrySend(payload, 4000)) return;
        Log.Write("menu host unreachable, respawning");
        lock (_lock) { try { _host?.Kill(); } catch { } _host = null; }
        EnsureSpawned();
        if (TrySend(payload, 5000)) return;
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

        // 预热放后台线程：管道循环立刻可用（否则预热的几秒里右键请求连不上管道，
        // 客户端误判 host 死了把它杀掉重拉——旧版右键时灵时不灵的帮凶之一）。
        // 内容：①背景菜单扩展在本进程加载（实测安全）；②对桌面上所有文件类型跑一遍
        // 牺牲进程探针（文件项扩展会 fail-fast 带走进程，不能在本进程试），
        // 之后任何图标的首次右键都不用再付探针延迟。
        var warm = new Thread(() =>
        {
            try
            {
                ShellContextMenu.Prewarm(DesktopItemProvider.UserDesktop, _ownerHwnd);
                Log.Write("menu host prewarmed");
            }
            catch (Exception ex) { Log.Write("menu host prewarm failed: " + ex.Message); }
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(DesktopItemProvider.UserDesktop))
                    ProbeSafe(entry); // 按类型去重，每种只探一次
                Log.Write("menu host probes done");
            }
            catch (Exception ex) { Log.Write("menu host probe sweep failed: " + ex.Message); }
        }) { IsBackground = true };
        warm.SetApartmentState(ApartmentState.STA);
        warm.Start();

        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                server.WaitForConnection();
                string payload;
                using (var r = new StreamReader(server, Encoding.UTF8)) payload = r.ReadToEnd();
                var parts = payload.Split(US);
                if (parts.Length < 5) continue;
                int x = int.Parse(parts[1]), y = int.Parse(parts[2]);
                var desktopHwnd = new IntPtr(long.Parse(parts[3]));
                var paths = parts.Skip(4).ToArray();
                Log.Write($"menu host: {parts[0]} request at {x},{y} ({paths.Length} path(s))");
                bool full = parts[0] != "bg" && ProbeSafe(paths[0]);
                _menuOpen = true;
                try
                {
                    if (parts[0] == "bg")
                        ShellContextMenu.ShowBackground(paths[0], _ownerHwnd, x, y, desktopHwnd);
                    else if (full)
                        ShellContextMenu.Show(paths, _ownerHwnd, x, y, desktopHwnd);
                    else
                        ShellContextMenu.ShowDegraded(paths, _ownerHwnd, x, y, desktopHwnd);
                }
                finally { _menuOpen = false; }
            }
            catch (Exception ex) { Log.Write("menu host loop error: " + ex.Message); }
        }
    }

    // ── 文件菜单安全性探针（结果按文件类型缓存） ──────────────

    private static readonly Dictionary<string, bool> _safeByKind = new();
    private static readonly object _probeGate = new(); // 预探针线程与请求线程都会进来，串行化

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
        lock (_probeGate)
        {
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
}
