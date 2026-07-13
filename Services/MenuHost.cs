using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MacDesk.Interop;

namespace MacDesk.Services;

/// <summary>
/// 常驻右键菜单子进程（<c>MacDesk.exe --menuhost &lt;mainPid&gt;</c>）。
///
/// v2（默认，settings.MenuInMainProcess）：host 只构建——QueryContextMenu 加载第三方
/// 扩展（崩了不伤主进程）→ 强制填充懒加载子菜单 → MenuSnapshot 摘成数据树 → 回传主进程，
/// 主进程 UI 线程重建原生菜单同线程 TrackPopupMenu（前台战争终极解），选中的 shell 命令
/// id 回传 host 由同一 IContextMenu 实例 InvokeCommand。
/// 协议（管道 MacDesk.MenuHost.v2，帧 = 4 字节小端长度 + UTF8）：
///   请求帧：verb US x US y US hwnd US path[ US path...]（verb = files2 | bg2）
///   应答帧：JSON {Kind: "native"|"degraded"|"error", Items:[...]}
///   命令帧（仅 native）：4 字节小端 int（shell cmd id，0 = 取消/本地命令）
///
/// 旧路径（settings 关掉 v2 时）：host 内 TrackPopupMenu + settle-wait + 瞬灭重试，
/// 协议同旧版（管道 MacDesk.MenuHost，每请求一次连接，US 分隔，无应答）。
/// 切换需重启 MacDesk（host 按启动时的设置选一种循环）。
///
/// 菜单数据树不再缓存（PR #6 被驳回——缓存的 IContextMenu 绑定的是建缓存时的文件，
/// 命中后 InvokeCommand 会作用于错误文件，且同扩展名文件的菜单文本因文件名嵌入而不同）。
/// 改为纯预热：启动后对每种安全文件类型跑一次 QueryContextMenu（用真实文件），
/// 不缓存结果，只利用这次调用来预热 shell 扩展内部的 DLL 缓存，
/// 用户真正右键时扩展已是热的，延迟自然降低。
/// </summary>
internal static class MenuHost
{
    private const string PipeName = "MacDesk.MenuHost";
    private const string PipeNameV2 = "MacDesk.MenuHost.v2";
    private const char US = '\x1F';

    private sealed class Reply
    {
        public string Kind { get; set; } = "";
        public List<MenuSnapshot.Item>? Items { get; set; }
    }

    // ── 帧 IO（v2 双工协议） ──────────────────────────────────

    private static void WriteFrame(Stream s, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
        s.Write(len, 0, 4);
        s.Write(data, 0, data.Length);
        s.Flush();
    }

    private static byte[]? ReadFrame(Stream s)
    {
        var len = ReadExact(s, 4);
        if (len == null) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n is < 0 or > 64 * 1024 * 1024) return null;
        return ReadExact(s, n);
    }

    private static byte[]? ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(buf, off, n - off);
            if (r <= 0) return null;
            off += r;
        }
        return buf;
    }

    // ── host STA 侧的泵等待 ──────────────────────────────────
    // shell 的"属性"等异步动词把数据对象留在 host 的 STA 上、另开线程建属性页，
    // 初始化时要编组回调到本 STA。host 闲时死等管道（不泵消息）会把这些回调卡住——
    // 机主实测属性窗口要等好多秒才弹出。所有空闲等待改 MsgWaitForMultipleObjects+泵。

    private static void PumpUntilSignaled(WaitHandle h)
    {
        var handles = new[] { h.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            uint r = Native.MsgWaitForMultipleObjects(1, handles, false, 0xFFFFFFFF, Native.QS_ALLINPUT);
            if (r == 1) // 有消息：泵掉再回去等
            {
                while (Native.PeekMessageW(out var m, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                {
                    Native.TranslateMessage(ref m);
                    Native.DispatchMessageW(ref m);
                }
                continue;
            }
            return; // 句柄有信号（r==0）或等待失败：交回调用方
        }
    }

    private static byte[]? ReadFramePumping(Stream s)
    {
        var len = ReadExactPumping(s, 4);
        if (len == null) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n is < 0 or > 64 * 1024 * 1024) return null;
        return ReadExactPumping(s, n);
    }

    private static byte[]? ReadExactPumping(Stream s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            var t = s.ReadAsync(buf, off, n - off);
            PumpUntilSignaled(((IAsyncResult)t).AsyncWaitHandle);
            int r;
            try { r = t.GetAwaiter().GetResult(); }
            catch { return null; }
            if (r <= 0) return null;
            off += r;
        }
        return buf;
    }

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

    /// <summary>收起当前打开的菜单（点击别处/开新菜单前调用；只作用于旧路径的 host 菜单，
    /// v2 主进程菜单同线程模态、点击自然关闭，无需外部收）。</summary>
    public static void Dismiss() => CommandChannel.Signal("DismissMenu");

    /// <summary>请求走后台线程：host 死了要重拉时（最长几秒）别卡 UI 线程。</summary>
    private static void Request(string verb, int x, int y, IntPtr desktopHwnd, string[] paths) =>
        Task.Run(() =>
        {
            if (Settings.Load().MenuInMainProcess) RequestCoreV2(verb, x, y, desktopHwnd, paths);
            else RequestCore(verb, x, y, desktopHwnd, paths);
        });

    // ── 客户端 v2：拿序列化菜单 → 主线程 Track → 回传命令 ─────

    private static void RequestCoreV2(string verb, int x, int y, IntPtr desktopHwnd, string[] paths)
    {
        // 阶梯耐心同旧版：host 忙（探针/上一个菜单）≠ 死，只有进程真没了才重拉
        if (TryExchangeV2(verb, x, y, desktopHwnd, paths, 1500)) return;
        bool alive;
        lock (_lock) alive = _host is { HasExited: false };
        if (alive && TryExchangeV2(verb, x, y, desktopHwnd, paths, 4000)) return;
        Log.Write("menu host (v2) unreachable, respawning");
        lock (_lock) { try { _host?.Kill(); } catch { } _host = null; }
        EnsureSpawned();
        if (TryExchangeV2(verb, x, y, desktopHwnd, paths, 5000)) return;
        // 双保险：退化为旧的一次性子进程（host 内 track，settle-wait 路径）
        Log.Write("menu host (v2) still unreachable, falling back to one-shot helper");
        FallbackOneShot(verb, x, y, paths);
    }

    private static bool TryExchangeV2(string verb, int x, int y, IntPtr desktopHwnd, string[] paths, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeNameV2, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            var payload = $"{verb}2{US}{x}{US}{y}{US}{(long)desktopHwnd}{US}{string.Join(US, paths)}";
            WriteFrame(pipe, Encoding.UTF8.GetBytes(payload));

            var replyBytes = ReadFrame(pipe); // host 构建+捕获（首次可能含探针，秒级）
            if (replyBytes == null) { Log.Write("menu v2: empty reply"); return false; }

            // 应答已到手，之后无论出什么错都不许走阶梯重试——菜单可能已经弹过，
            // 重试会让它凭空再弹一次（用户视角是灵异事件）
            try
            {
                var reply = JsonSerializer.Deserialize<Reply>(replyBytes);
                if (reply == null || reply.Kind == "error") { Log.Write("menu v2: host build failed"); return true; }

                List<MenuSnapshot.Item> items;
                bool shellSide = reply.Kind == "native" && reply.Items != null;
                if (shellSide)
                {
                    items = reply.Items!;
                    NativeMenuPresenter.Catalog(items); // 只收 shell 项（自定义项追加之前）
                    if (verb == "bg") items.AddRange(NativeMenuPresenter.CustomBackgroundItems());
                }
                else
                {
                    items = NativeMenuPresenter.DegradedFileItems(paths); // 探针判定该类型必崩
                }

                uint cmd = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (verb == "files")
                    {
                        NativeMenuPresenter.AppendRenameItem(items, paths); // shell 菜单没有重命名（见方法注释）
                        NativeMenuPresenter.AppendSelectionItems(items, paths);
                        NativeMenuPresenter.AppendFolderStackItem(items, paths); // 文件夹堆叠开关
                        NativeMenuPresenter.AppendLeRunItem(items, paths); // STA 内解析 .lnk
                    }
                    return NativeMenuPresenter.Track(desktopHwnd, items, x, y);
                });
                Log.Write($"menu v2: {verb} shown ({items.Count} items, kind={reply.Kind}), cmd=0x{cmd:X}");

                bool handledLocally = cmd == 0 || NativeMenuPresenter.DispatchLocal(cmd, paths);
                if (shellSide)
                {
                    var cmdBytes = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(cmdBytes, handledLocally ? 0 : (int)cmd);
                    WriteFrame(pipe, cmdBytes); // host 侧 InvokeCommand（或 0 = 直接收工）
                }
            }
            catch (Exception ex) { Log.Write("menu v2 post-reply failed: " + ex.Message); }
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (Exception ex)
        {
            Log.Write($"menu v2 exchange failed: {ex.Message}");
            return false;
        }
    }

    // ── 客户端旧路径（host 内 track） ─────────────────────────

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
        FallbackOneShot(verb, x, y, paths);
    }

    private static void FallbackOneShot(string verb, int x, int y, string[] paths)
    {
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

        // 点别处收菜单（仅旧路径用）：主进程发命名事件，我们给 owner 发 WM_CANCELMODE。
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
        // 之后任何图标的首次右键都不用再付探针延迟；
        // ③对每种安全类型跑一次完整 BuildFileMenu（用真实文件），
        // 不缓存结果，只利用这次调用来预热 shell 扩展内部的 DLL 缓存。
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

                // ③ 预热构建：若探针崩了 _safeByKind 空/半空，ProbeSafe 退化为持锁 8s
                var swWarm = Stopwatch.StartNew();
                int warmed = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(DesktopItemProvider.UserDesktop))
                {
                    if (!ProbeSafe(entry)) continue;
                    using var built = ShellContextMenu.BuildFileMenu(new[] { entry }, _ownerHwnd);
                    if (built != null) warmed++;
                }
                Log.Write($"menu host: pure-prewarmed {warmed} file types in {swWarm.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { Log.Write("menu host probe/prewarm failed: " + ex.Message); }
        }) { IsBackground = true };
        warm.SetApartmentState(ApartmentState.STA);
        warm.Start();

        // 两种循环二选一（按启动时设置；切换需重启 MacDesk）：owner 窗口在本线程，
        // 构建期间扩展对 owner 的同线程 SendMessage 直接派发，不依赖消息泵。
        if (Settings.Load().MenuInMainProcess) RunV2Loop();
        else RunLegacyLoop();
    }

    /// <summary>v2 循环：构建 → 序列化回传 → 等主进程 Track 结果 → InvokeCommand。</summary>
    private static void RunV2Loop()
    {
        Log.Write("menu host: v2 (serialize-to-main) loop");
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeNameV2, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var ar = server.BeginWaitForConnection(null, null);
                PumpUntilSignaled(ar.AsyncWaitHandle); // 等连接期间泵消息（属性页等异步动词的编组回调靠它放行）
                server.EndWaitForConnection(ar);
                var req = ReadFramePumping(server);
                if (req == null) continue;
                var parts = Encoding.UTF8.GetString(req).Split(US);
                if (parts.Length < 5) continue;
                var paths = parts.Skip(4).ToArray();
                Log.Write($"menu host v2: {parts[0]} request ({paths.Length} path(s))");

                bool full = parts[0] == "bg2" || ProbeSafe(paths[0]);
                if (!full)
                {
                    WriteFrame(server, JsonSerializer.SerializeToUtf8Bytes(new Reply { Kind = "degraded" }));
                    continue;
                }

                // 每次右键全量构建（预热已暖 shell 扩展内部缓存，不再缓存菜单数据树）
                var sw = Stopwatch.StartNew();
                using var built = parts[0] == "bg2"
                    ? ShellContextMenu.BuildBackgroundMenu(paths[0], _ownerHwnd)
                    : ShellContextMenu.BuildFileMenu(paths, _ownerHwnd);
                if (built == null)
                {
                    WriteFrame(server, JsonSerializer.SerializeToUtf8Bytes(new Reply { Kind = "error" }));
                    continue;
                }
                MenuSnapshot.ForceInit(built.MenuObj, built.HMenu, 0);
                var items = MenuSnapshot.Capture(built.HMenu, built.MenuObj);
                WriteFrame(server, JsonSerializer.SerializeToUtf8Bytes(new Reply { Kind = "native", Items = items }));
                Log.Write($"menu host v2: captured {items.Count} items for {parts[0]} in {sw.ElapsedMilliseconds}ms");
                var menuObj = built.MenuObj; // 保留引用：built.Dispose 只销毁 HMENU，IContextMenu 是 COM 对象仍存活
                var swWait = Stopwatch.StartNew(); // 计命令等待+执行时间（用户交互窗口）

                // 菜单在主进程开着期间保持 IContextMenu 存活；用户关菜单后收命令帧。
                // 泵等待：上一个属性页/异步动词的编组回调不因本次等待而停摆
                var cmdBytes = ReadFramePumping(server);
                int cmd = cmdBytes is { Length: 4 } ? BinaryPrimitives.ReadInt32LittleEndian(cmdBytes) : 0;
                if (cmd is > 0 and <= 0x6FFF)
                {
                    ShellContextMenu.InvokeShellCmd(menuObj, cmd - 1, _ownerHwnd);
                    Log.Write($"menu host v2: invoked shell cmd {cmd}");
                }
                Log.Write($"menu host v2: {parts[0]} request done ({swWait.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex) { Log.Write("menu host v2 loop error: " + ex.Message); }
        }
    }

    /// <summary>旧循环：host 内 TrackPopupMenu（settle-wait + 瞬灭重试）。</summary>
    private static void RunLegacyLoop()
    {
        Log.Write("menu host: legacy (in-host track) loop");
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
