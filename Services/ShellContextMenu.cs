using System.IO;
using System.Runtime.InteropServices;
using static MacDesk.Interop.Native;

namespace MacDesk.Services;

/// <summary>
/// 原生 shell 右键菜单转发（在隔离子进程里运行，第三方扩展崩溃不伤主进程）。
/// - Show：单个/多个文件项的 IContextMenu
/// - ShowBackground：桌面文件夹背景菜单（新建/粘贴/刷新…）+ 追加 MacDesk 自定义项
/// 自定义项通过命名事件通知主进程（见 CommandChannel）。
/// </summary>
internal static class ShellContextMenu
{
    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private const uint CMF_NORMAL = 0x0;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    // 后台进程弹菜单时淡入动画不渲染，菜单是白块、划过才显示（26200 真机实测）→ 跳过动画
    private const uint TPM_NOANIMATION = 0x4000;
    private const uint TPM_FLAGS = TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NOANIMATION;
    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MF_CHECKED = 0x0008, MF_POPUP = 0x0010;

    // 自定义菜单命令 ID（> QueryContextMenu 的 idCmdLast）
    private const uint ID_ARRANGE = 0x7001, ID_UNDO = 0x7002, ID_TOGGLE = 0x7003, ID_QUIT = 0x7004,
                       ID_AUTOSTART = 0x7005, ID_SORT_NAME = 0x7006, ID_SORT_DATE = 0x7007,
                       ID_SORT_SIZE = 0x7008, ID_SORT_KIND = 0x7009, ID_FREE = 0x700A,
                       ID_SETTINGS = 0x700B;

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>TrackPopupMenu 期间的活动 shell 菜单对象（owner 窗口转发菜单消息用）。</summary>
    private static object? _activeMenu;

    /// <summary>owner 窗口收到菜单相关消息时调用：转发给 IContextMenu2.HandleMenuMsg。</summary>
    public static (bool Handled, IntPtr Result) ForwardMenuMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_activeMenu is IContextMenu2 cm2)
        {
            try
            {
                cm2.HandleMenuMsg((uint)msg, wParam, lParam);
                // WM_DRAWITEM / WM_MEASUREITEM 处理后须返回 TRUE
                return (true, msg is 0x002B or 0x002C ? (IntPtr)1 : IntPtr.Zero);
            }
            catch { }
        }
        return (false, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out object ppshf);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, System.Text.StringBuilder lpString, int cchMax, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);

    private const uint MF_BYPOSITION = 0x0400;

    /// <summary>按用户黑名单（settings.json 的 MenuBlacklist，子串不分大小写）剥掉菜单项，
    /// 并清理由此产生的重复/边缘分隔线。设置 GUI 之前的过渡配置形态。</summary>
    private static void StripBlacklisted(IntPtr hMenu)
    {
        List<string> bl;
        try { bl = Settings.Load().MenuBlacklist; } catch { return; }
        if (bl is not { Count: > 0 }) return;

        for (int i = GetMenuItemCount(hMenu) - 1; i >= 0; i--)
        {
            var sb = new System.Text.StringBuilder(512);
            GetMenuStringW(hMenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
            var txt = sb.ToString();
            if (txt.Length > 0 && bl.Any(b => !string.IsNullOrWhiteSpace(b) && txt.Contains(b, StringComparison.OrdinalIgnoreCase)))
                RemoveMenu(hMenu, (uint)i, MF_BYPOSITION);
        }

        bool prevSep = true; // 也顺带删掉开头的分隔线
        for (int i = 0; i < GetMenuItemCount(hMenu); )
        {
            bool sep = (GetMenuState(hMenu, (uint)i, MF_BYPOSITION) & MF_SEPARATOR) != 0;
            if (sep && prevSep) { RemoveMenu(hMenu, (uint)i, MF_BYPOSITION); continue; }
            prevSep = sep;
            i++;
        }
        int n = GetMenuItemCount(hMenu);
        if (n > 0 && (GetMenuState(hMenu, (uint)(n - 1), MF_BYPOSITION) & MF_SEPARATOR) != 0)
            RemoveMenu(hMenu, (uint)(n - 1), MF_BYPOSITION);
    }

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    // uxtheme 未公开序号导出（Win10 1809+ 稳定存在，SAB/EP 同款）：让 Win32 经典菜单跟随系统深色模式
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    /// <summary>经典 TrackPopupMenu 菜单换现代观感：跟随系统深色模式（Win11 圆角系统自带）。</summary>
    public static void EnableModernMenuTheme()
    {
        try { SetPreferredAppMode(1 /* AllowDark */); FlushMenuThemes(); }
        catch (Exception ex) { Log.Write("menu dark mode unavailable: " + ex.Message); }
    }

    /// <summary>预热：走一遍 QueryContextMenu 让第三方扩展 DLL 现在加载（不显示菜单）。
    /// owner 必须传真实窗口——传 IntPtr.Zero 会被个别扩展 fail-fast（真机 0xc0000409 实测）。</summary>
    public static void Prewarm(string desktopFolder, IntPtr ownerHwnd)
    {
        IntPtr pidl = IntPtr.Zero, hMenu = IntPtr.Zero;
        try
        {
            SHGetDesktopFolder(out object desktopObj);
            var desktop = (IShellFolder)desktopObj;
            SHParseDisplayName(desktopFolder, IntPtr.Zero, out pidl, 0, out _);
            if (pidl == IntPtr.Zero) return;
            var iidFolder = IID_IShellFolder;
            desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out object folderObj);
            var folder = (IShellFolder)folderObj;
            var iidMenu = IID_IContextMenu;
            hMenu = CreatePopupMenu();
            using (new ManagedHandlerShield())
            {
                folder.CreateViewObject(ownerHwnd, ref iidMenu, out object menuObj);
                ((IContextMenu)menuObj).QueryContextMenu(hMenu, 0, 1, 0x6FFF, CMF_NORMAL);
            }
            Log.Write("prewarm: bg menu ok");
            // 文件项扩展不在这里预热：这台机上文件项 QueryContextMenu 会 fail-fast（0xc0000409），
            // 崩了会带走 host。文件项统一走牺牲进程探针（--menuprobe，见 ProbeFile/MenuHost.ProbeSafe）。
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    /// <summary>
    /// 牺牲进程探针（--menuprobe 模式）：只 QueryContextMenu 不显示。加载第三方扩展若 fail-fast，
    /// 崩的是本探针进程（退出码非 0），host 据此判定该文件类型必须用降级菜单。成功则顺带预热了 DLL。
    /// </summary>
    public static bool ProbeFile(string path, IntPtr ownerHwnd)
    {
        IntPtr pidl = IntPtr.Zero, hMenu = IntPtr.Zero;
        try
        {
            SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (pidl == IntPtr.Zero) return false;
            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidl, ref iidFolder, out object pf, out IntPtr child) != 0) return false;
            var iidMenu = IID_IContextMenu;
            hMenu = CreatePopupMenu();
            int hr;
            using (new ManagedHandlerShield())
            {
                ((IShellFolder)pf).GetUIObjectOf(ownerHwnd, 1, new[] { child }, ref iidMenu, IntPtr.Zero, out object fm);
                hr = ((IContextMenu)fm).QueryContextMenu(hMenu, 0, 1, 0x6FFF, CMF_NORMAL);
            }
            Log.Write($"menu probe ok: {path} hr=0x{hr:X8}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Log.Write($"menu probe failed (managed): {path}: {ex.Message}");
            return false;
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    // 降级文件菜单：探针判定该类型的原生菜单必崩时使用。核心动词回传主进程
    // 对当前选中执行（主进程已有实现），"打开方式/属性"由本进程直接调系统。
    private const uint ID_D_OPEN = 0x7101, ID_D_OPENWITH = 0x7102, ID_D_CUT = 0x7103, ID_D_COPY = 0x7104,
                       ID_D_RENAME = 0x7105, ID_D_DELETE = 0x7106, ID_D_PROPS = 0x7107;

    public static void ShowDegraded(string[] paths, IntPtr ownerHwnd, int screenX, int screenY, IntPtr desktopHwnd = default)
    {
        IntPtr hMenu = CreatePopupMenu();
        try
        {
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_OPEN, "打开");
            if (paths.Length == 1 && !paths[0].StartsWith("::"))
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_OPENWITH, "打开方式…");
            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_CUT, "剪切");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_COPY, "复制");
            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            if (paths.Length == 1) AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_RENAME, "重命名");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_DELETE, "删除");
            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_D_PROPS, "属性");

            int cmd = TrackWithRetry(hMenu, ownerHwnd, screenX, screenY, desktopHwnd);
            PostMessage(ownerHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"degraded file menu shown for {paths.Length} item(s), cmd=0x{cmd:X}");
            switch ((uint)cmd)
            {
                case ID_D_OPEN: CommandChannel.Signal("OpenSelection"); break;
                case ID_D_OPENWITH:
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            "rundll32.exe", $"shell32.dll,OpenAs_RunDLL {paths[0]}") { UseShellExecute = false });
                    }
                    catch (Exception ex) { Log.Write("OpenAs failed: " + ex.Message); }
                    break;
                case ID_D_CUT: CommandChannel.Signal("CutSelection"); break;
                case ID_D_COPY: CommandChannel.Signal("CopySelection"); break;
                case ID_D_RENAME: CommandChannel.Signal("RenameSelection"); break;
                case ID_D_DELETE: CommandChannel.Signal("DeleteSelection"); break;
                case ID_D_PROPS: CommandChannel.Signal("PropertiesSelection"); break;
            }
        }
        finally { DestroyMenu(hMenu); }
    }

    /// <summary>构建好的 shell 菜单：HMENU + 活的 IContextMenu（Invoke 用）+ 待释放 pidl。</summary>
    internal sealed class BuiltShellMenu : IDisposable
    {
        public IntPtr HMenu;
        public object MenuObj = null!;
        public IntPtr[] Pidls = Array.Empty<IntPtr>();

        public void Dispose()
        {
            if (HMenu != IntPtr.Zero) { DestroyMenu(HMenu); HMenu = IntPtr.Zero; }
            foreach (var p in Pidls) if (p != IntPtr.Zero) CoTaskMemFree(p);
            Pidls = Array.Empty<IntPtr>();
        }
    }

    /// <summary>只构建不显示：文件项菜单（多个路径必须同属一个父文件夹，调用方保证）。</summary>
    public static BuiltShellMenu? BuildFileMenu(string[] paths, IntPtr ownerHwnd)
    {
        if (paths.Length == 0) return null;
        var pidls = new IntPtr[paths.Length];
        var children = new IntPtr[paths.Length];
        var built = new BuiltShellMenu { Pidls = pidls };
        try
        {
            var iidFolder = IID_IShellFolder;
            object? folderObj = null;
            for (int i = 0; i < paths.Length; i++)
            {
                SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _);
                if (pidls[i] == IntPtr.Zero) { Log.Write($"file menu: parse failed for {paths[i]}"); built.Dispose(); return null; }
                if (SHBindToParent(pidls[i], ref iidFolder, out object fo, out children[i]) != 0)
                { Log.Write($"file menu: SHBindToParent failed for {paths[i]}"); built.Dispose(); return null; }
                folderObj ??= fo; // 同父，取第一个
            }
            var folder = (IShellFolder)folderObj!;

            var iidMenu = IID_IContextMenu;
            object menuObj;
            int hr;
            built.HMenu = CreatePopupMenu();
            using (new ManagedHandlerShield())
            {
                folder.GetUIObjectOf(ownerHwnd, (uint)paths.Length, children, ref iidMenu, IntPtr.Zero, out menuObj);
                hr = ((IContextMenu)menuObj).QueryContextMenu(built.HMenu, 0, 1, 0x6FFF, CMF_NORMAL);
            }
            if (hr < 0) { Log.Write($"file menu: QueryContextMenu hr=0x{hr:X8}"); built.Dispose(); return null; }
            built.MenuObj = menuObj;
            StripBlacklisted(built.HMenu);
            return built;
        }
        catch (Exception ex) { Log.Write("file menu build failed: " + ex); built.Dispose(); return null; }
    }

    /// <summary>跨进程回传的 shell 命令 id → 同一个 IContextMenu 实例执行。</summary>
    public static void InvokeShellCmd(object menuObj, int verbOffset, IntPtr owner) =>
        Invoke((IContextMenu)menuObj, verbOffset, owner);

    /// <summary>侦察模式（--menudump [path ...]）：构建背景菜单与给定文件的菜单，
    /// 强制填充懒加载子菜单后把结构树写日志——序列化路径按真机数据设计，不靠猜。</summary>
    public static void DumpMenus(string[] paths, IntPtr ownerHwnd)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var bg = BuildBackgroundMenu(DesktopItemProvider.UserDesktop, ownerHwnd))
            {
                if (bg != null)
                {
                    MenuSnapshot.ForceInit(bg.MenuObj, bg.HMenu, 0);
                    var items = MenuSnapshot.Capture(bg.HMenu, bg.MenuObj);
                    Log.Write($"=== menudump bg ({sw.ElapsedMilliseconds}ms) ===");
                    MenuSnapshot.DumpLog(items);
                }
                else Log.Write("menudump: bg build failed");
            }
            foreach (var p in paths)
            {
                sw.Restart();
                using var fm = BuildFileMenu(new[] { p }, ownerHwnd);
                if (fm == null) { Log.Write($"menudump: file build failed for {p}"); continue; }
                MenuSnapshot.ForceInit(fm.MenuObj, fm.HMenu, 0);
                var items = MenuSnapshot.Capture(fm.HMenu, fm.MenuObj);
                Log.Write($"=== menudump file {p} ({sw.ElapsedMilliseconds}ms) ===");
                MenuSnapshot.DumpLog(items);
            }
        }
        catch (Exception ex) { Log.Write("menudump failed: " + ex); }
    }

    /// <summary>文件项菜单（旧路径：host 内 TrackPopupMenu，settle-wait+重试兜前台风暴）。</summary>
    public static void Show(string[] paths, IntPtr ownerHwnd, int screenX, int screenY, IntPtr desktopHwnd = default)
    {
        try
        {
            using var built = BuildFileMenu(paths, ownerHwnd);
            if (built == null) return;

            _activeMenu = built.MenuObj;
            int cmd;
            try { cmd = TrackWithRetry(built.HMenu, ownerHwnd, screenX, screenY, desktopHwnd); }
            finally { _activeMenu = null; }
            PostMessage(ownerHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"file menu shown for {paths.Length} item(s), cmd={cmd}");
            if (cmd > 0) Invoke((IContextMenu)built.MenuObj, cmd - 1, ownerHwnd);
        }
        catch (Exception ex) { Log.Write("file menu failed: " + ex); }
    }

    // ── 托管 shell 扩展隔离 ───────────────────────────────────
    // 真凶复盘（2026-07-06 二分定位）：Locale Emulator 的 LEContextMenuHandler 是
    // .NET Framework 写的 shell 扩展（InprocServer32 = mscoree.dll）。本进程是 .NET 10，
    // 加载 Framework CLR 的 COM 组件直接 fail-fast 0xC0000409——任何托管 shell 扩展在
    // 我们进程里都必炸（Explorer 不炸是因为它没载新 CLR）。对策：QueryContextMenu 的
    // 毫秒级窗口内把这些 CLSID 临时写入系统 Blocked 键（shell 聚合时跳过它们），
    // 完事立刻移除——Explorer 平时的菜单不受影响。

    private static readonly string[] HandlerRoots =
    {
        @"*\shellex\ContextMenuHandlers",
        @"AllFilesystemObjects\shellex\ContextMenuHandlers",
        @"Folder\shellex\ContextMenuHandlers",
        @"Directory\shellex\ContextMenuHandlers",
        @"Directory\Background\shellex\ContextMenuHandlers",
        @"DesktopBackground\shellex\ContextMenuHandlers",
        @"lnkfile\shellex\ContextMenuHandlers",
        @"exefile\shellex\ContextMenuHandlers",
    };

    private static readonly Lazy<string[]> ManagedHandlerClsids = new(() =>
    {
        var list = new List<string>();
        try
        {
            foreach (var root in HandlerRoots)
            {
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(root);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var hk = key.OpenSubKey(sub);
                    // CLSID 的两种注册姿势：默认值 = CLSID；或键名 = CLSID、默认值 = 显示名（LE 是后者）
                    var val = hk?.GetValue(null) as string;
                    string? clsid = val is not null && val.StartsWith('{') ? val
                        : sub.StartsWith('{') ? sub : null;
                    if (clsid == null) continue;
                    using var inproc = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
                    if (inproc?.GetValue(null) is string dll &&
                        dll.Contains("mscoree", StringComparison.OrdinalIgnoreCase) &&
                        !list.Contains(clsid, StringComparer.OrdinalIgnoreCase))
                        list.Add(clsid);
                }
            }
        }
        catch (Exception ex) { Log.Write("managed handler scan failed: " + ex.Message); }
        if (list.Count > 0)
            Log.Write($"managed (mscoree) shell handlers to bypass: {string.Join(", ", list)}");
        return list.ToArray();
    });

    private const string BlockedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>菜单构建期间临时屏蔽托管扩展（Dispose 即恢复）。</summary>
    private sealed class ManagedHandlerShield : IDisposable
    {
        private readonly string[] _blocked;

        public ManagedHandlerShield()
        {
            _blocked = ManagedHandlerClsids.Value;
            if (_blocked.Length == 0) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(BlockedKeyPath);
                foreach (var c in _blocked) key.SetValue(c, "");
            }
            catch (Exception ex) { Log.Write("handler shield up failed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (_blocked.Length == 0) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BlockedKeyPath, writable: true);
                if (key == null) return;
                foreach (var c in _blocked) key.DeleteValue(c, throwOnMissingValue: false);
            }
            catch (Exception ex) { Log.Write("handler shield down failed: " + ex.Message); }
        }
    }

    /// <summary>排空滞留在队列里的 WM_CANCELMODE（迟到的 Dismiss 信号会把新菜单秒杀）。</summary>
    private static void DrainCancelMode(IntPtr ownerHwnd)
    {
        while (PeekMessageW(out _, ownerHwnd, WM_CANCELMODE, WM_CANCELMODE, PM_REMOVE)) { }
    }

    /// <summary>
    /// TrackPopupMenu + 瞬灭重试。点击桌面会让 Progman 父链**异步**激活，激活落地时会把
    /// 别的线程刚开出的菜单扫掉（真机实测：菜单 ~85ms 自灭 cmd=0，时序抖动决定谁存活，
    /// 表现为"右键时灵时不灵"）。开出 <300ms 即灭且期间没有任何按键/鼠标按下 = 误杀 → 重开。
    /// </summary>
    private static int TrackWithRetry(IntPtr hMenu, IntPtr ownerHwnd, int x, int y, IntPtr desktopHwnd)
    {
        // 等鼠标键全部松开再开菜单：菜单在按住的按键正下方"物化"，紧接着的抬起会被判成
        // 选中菜单项（机主实测：右键图标偶发直接触发 File Locksmith）
        for (int i = 0; i < 40 && ((GetAsyncKeyState(0x01) | GetAsyncKeyState(0x02)) & 0x8000) != 0; i++)
            Thread.Sleep(10);

        // 等前台风暴平息再开菜单：上一个菜单被点击关闭时，系统会把前台异步归还给
        // 我们的主窗口、再到 Progman（真机日志：两发迟到夺台，各杀一个菜单，连击时每次
        // 都要三开才站住）。轮询前台直到 75ms 无变化（最多等 300ms），一次开成、无闪烁。
        // 注：AttachThreadInput 共享输入队列挡不住这种失活（实测），别再试了。
        int t0 = Environment.TickCount;
        IntPtr lastFg = GetForegroundWindow();
        int stableSince = t0;
        while (Environment.TickCount - t0 < 300)
        {
            Thread.Sleep(15);
            var f = GetForegroundWindow();
            if (f != lastFg) { lastFg = f; stableSince = Environment.TickCount; }
            else if (Environment.TickCount - stableSince >= 75) break;
        }

        for (int attempt = 0; ; attempt++)
        {
            ForceForeground(ownerHwnd);
            DrainCancelMode(ownerHwnd);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int cmd = TrackPopupMenuEx(hMenu, TPM_FLAGS, x, y, ownerHwnd, IntPtr.Zero);
            if (cmd != 0 || sw.ElapsedMilliseconds > 300 || attempt >= 3) return cmd;
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 ||
                (GetAsyncKeyState(0x1B) & 0x8000) != 0) return cmd; // 用户真在点/按 Esc = 真取消
            var fg = GetForegroundWindow();
            Log.Write($"menu insta-cancelled in {sw.ElapsedMilliseconds}ms (fg={fg} cls={WindowClass(fg)}), retrying");
            Thread.Sleep(50); // 给迟到的异步激活一点落地时间，别让它再杀一次
        }
    }

    /// <summary>
    /// 后台进程没有前台权限，SetForegroundWindow 会被拒 → TrackPopupMenu 的菜单
    /// 收不到"点击外部关闭"（用户桌面上残留一堆幽灵菜单的元凶）。
    /// AttachThreadInput 到当前前台线程借权限，是托盘/Shell 程序的标准做法。
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            var fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint self = GetCurrentThreadId();
            if (fgThread != self)
            {
                AttachThreadInput(fgThread, self, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(fgThread, self, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch { }
    }

    /// <summary>只构建不显示：桌面背景 shell 菜单（新建/粘贴/查看等，不含 MacDesk 自定义项）。</summary>
    public static BuiltShellMenu? BuildBackgroundMenu(string folderPath, IntPtr ownerHwnd)
    {
        IntPtr pidl = IntPtr.Zero;
        var built = new BuiltShellMenu();
        try
        {
            SHGetDesktopFolder(out object desktopObj);
            var desktop = (IShellFolder)desktopObj;
            SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _);
            if (pidl == IntPtr.Zero) return null;
            built.Pidls = new[] { pidl };

            var iidFolder = IID_IShellFolder;
            desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out object folderObj);
            var folder = (IShellFolder)folderObj;

            var iidMenu = IID_IContextMenu;
            object menuObj;
            int hr;
            built.HMenu = CreatePopupMenu();
            using (new ManagedHandlerShield())
            {
                folder.CreateViewObject(ownerHwnd, ref iidMenu, out menuObj);
                hr = ((IContextMenu)menuObj).QueryContextMenu(built.HMenu, 0, 1, 0x6FFF, CMF_NORMAL);
            }
            if (hr < 0) { Log.Write($"bg menu: QueryContextMenu hr=0x{hr:X8}"); built.Dispose(); return null; }
            built.MenuObj = menuObj;
            StripBlacklisted(built.HMenu);
            return built;
        }
        catch (Exception ex) { Log.Write("bg menu build failed: " + ex); built.Dispose(); return null; }
    }

    /// <summary>桌面背景菜单（旧路径：host 内 TrackPopupMenu）+ MacDesk 自定义项。</summary>
    public static void ShowBackground(string folderPath, IntPtr ownerHwnd, int screenX, int screenY, IntPtr desktopHwnd = default)
    {
        try
        {
            using var built = BuildBackgroundMenu(folderPath, ownerHwnd);
            if (built == null) return;
            var hMenu = built.HMenu;

            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_ARRANGE, "按 mac 式网格整理");

            // 排序方式子菜单（一次性排序整理）
            IntPtr hSort = CreatePopupMenu();
            AppendMenuW(hSort, MF_STRING, (UIntPtr)ID_SORT_NAME, "名称");
            AppendMenuW(hSort, MF_STRING, (UIntPtr)ID_SORT_DATE, "修改日期");
            AppendMenuW(hSort, MF_STRING, (UIntPtr)ID_SORT_SIZE, "大小");
            AppendMenuW(hSort, MF_STRING, (UIntPtr)ID_SORT_KIND, "类型");
            AppendMenuW(hMenu, MF_POPUP, (UIntPtr)(ulong)hSort, "排序方式");

            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_UNDO, "撤销上次整理");
            AppendMenuW(hMenu, MF_STRING | (Settings.Load().FreePlacement ? MF_CHECKED : 0),
                (UIntPtr)ID_FREE, "自由摆放（不吸附网格）");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_TOGGLE, "显示/隐藏原生图标");
            AppendMenuW(hMenu, MF_STRING | (Autostart.IsEnabled() ? MF_CHECKED : 0),
                (UIntPtr)ID_AUTOSTART, "开机自启");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_SETTINGS, "MacDesk 设置…");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_QUIT, "退出 MacDesk (Ctrl+Alt+Q)");

            _activeMenu = built.MenuObj;
            int cmd;
            try { cmd = TrackWithRetry(hMenu, ownerHwnd, screenX, screenY, desktopHwnd); }
            finally { _activeMenu = null; }
            PostMessage(ownerHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"bg menu shown, cmd=0x{cmd:X}");
            switch ((uint)cmd)
            {
                case 0: return;
                case ID_ARRANGE: CommandChannel.Signal("Arrange"); return;
                case ID_UNDO: CommandChannel.Signal("Undo"); return;
                case ID_TOGGLE: CommandChannel.Signal("ToggleNative"); return;
                case ID_AUTOSTART: CommandChannel.Signal("ToggleAutostart"); return;
                case ID_FREE: CommandChannel.Signal("ToggleFree"); return;
                case ID_SORT_NAME: CommandChannel.Signal("SortName"); return;
                case ID_SORT_DATE: CommandChannel.Signal("SortDate"); return;
                case ID_SORT_SIZE: CommandChannel.Signal("SortSize"); return;
                case ID_SORT_KIND: CommandChannel.Signal("SortKind"); return;
                case ID_QUIT: CommandChannel.Signal("Quit"); return;
                case ID_SETTINGS: CommandChannel.Signal("OpenSettings"); return;
                default: Invoke((IContextMenu)built.MenuObj, cmd - 1, ownerHwnd); return;
            }
        }
        catch (Exception ex) { Log.Write("bg menu failed: " + ex); }
    }

    private static void Invoke(IContextMenu menu, int verbOffset, IntPtr owner)
    {
        var ici = new CMINVOKECOMMANDINFO
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            hwnd = owner,
            lpVerb = (IntPtr)verbOffset,
            nShow = 1,
        };
        menu.InvokeCommand(ref ici);
    }
}
