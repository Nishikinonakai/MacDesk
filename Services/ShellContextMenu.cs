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
                       ID_SORT_SIZE = 0x7008, ID_SORT_KIND = 0x7009, ID_FREE = 0x700A;

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
            folder.CreateViewObject(ownerHwnd, ref iidMenu, out object menuObj);
            hMenu = CreatePopupMenu();
            ((IContextMenu)menuObj).QueryContextMenu(hMenu, 0, 1, 0x6FFF, CMF_NORMAL);
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
            ((IShellFolder)pf).GetUIObjectOf(ownerHwnd, 1, new[] { child }, ref iidMenu, IntPtr.Zero, out object fm);
            hMenu = CreatePopupMenu();
            int hr = ((IContextMenu)fm).QueryContextMenu(hMenu, 0, 1, 0x6FFF, CMF_NORMAL);
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

    public static void ShowDegraded(string[] paths, IntPtr ownerHwnd, int screenX, int screenY)
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

            ForceForeground(ownerHwnd);
            DrainCancelMode(ownerHwnd);
            int cmd = TrackPopupMenuEx(hMenu, TPM_FLAGS, screenX, screenY, ownerHwnd, IntPtr.Zero);
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

    /// <summary>文件项菜单。多个路径必须同属一个父文件夹（调用方保证）。</summary>
    public static void Show(string[] paths, IntPtr ownerHwnd, int screenX, int screenY)
    {
        if (paths.Length == 0) return;
        var pidls = new IntPtr[paths.Length];
        var children = new IntPtr[paths.Length];
        IntPtr hMenu = IntPtr.Zero;
        try
        {
            var iidFolder = IID_IShellFolder;
            object? folderObj = null;
            for (int i = 0; i < paths.Length; i++)
            {
                SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _);
                if (pidls[i] == IntPtr.Zero) { Log.Write($"file menu: parse failed for {paths[i]}"); return; }
                if (SHBindToParent(pidls[i], ref iidFolder, out object fo, out children[i]) != 0)
                { Log.Write($"file menu: SHBindToParent failed for {paths[i]}"); return; }
                folderObj ??= fo; // 同父，取第一个
            }
            var folder = (IShellFolder)folderObj!;

            var iidMenu = IID_IContextMenu;
            folder.GetUIObjectOf(ownerHwnd, (uint)paths.Length, children, ref iidMenu, IntPtr.Zero, out object menuObj);
            var menu = (IContextMenu)menuObj;

            hMenu = CreatePopupMenu();
            int hr = menu.QueryContextMenu(hMenu, 0, 1, 0x6FFF, CMF_NORMAL);
            if (hr < 0) { Log.Write($"file menu: QueryContextMenu hr=0x{hr:X8}"); return; }

            ForceForeground(ownerHwnd);
            DrainCancelMode(ownerHwnd);
            _activeMenu = menuObj;
            int cmd;
            try { cmd = TrackPopupMenuEx(hMenu, TPM_FLAGS, screenX, screenY, ownerHwnd, IntPtr.Zero); }
            finally { _activeMenu = null; }
            PostMessage(ownerHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"file menu shown for {paths.Length} item(s), cmd={cmd}");
            if (cmd > 0) Invoke(menu, cmd - 1, ownerHwnd);
        }
        catch (Exception ex) { Log.Write("file menu failed: " + ex); }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            foreach (var p in pidls) if (p != IntPtr.Zero) CoTaskMemFree(p);
        }
    }

    /// <summary>排空滞留在队列里的 WM_CANCELMODE（迟到的 Dismiss 信号会把新菜单秒杀）。</summary>
    private static void DrainCancelMode(IntPtr ownerHwnd)
    {
        while (PeekMessageW(out _, ownerHwnd, WM_CANCELMODE, WM_CANCELMODE, PM_REMOVE)) { }
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

    /// <summary>桌面背景菜单（新建/粘贴/查看等）+ MacDesk 自定义项。</summary>
    public static void ShowBackground(string folderPath, IntPtr ownerHwnd, int screenX, int screenY)
    {
        IntPtr pidl = IntPtr.Zero, hMenu = IntPtr.Zero;
        try
        {
            SHGetDesktopFolder(out object desktopObj);
            var desktop = (IShellFolder)desktopObj;
            SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _);
            if (pidl == IntPtr.Zero) return;

            var iidFolder = IID_IShellFolder;
            desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out object folderObj);
            var folder = (IShellFolder)folderObj;

            var iidMenu = IID_IContextMenu;
            folder.CreateViewObject(ownerHwnd, ref iidMenu, out object menuObj);
            var menu = (IContextMenu)menuObj;

            hMenu = CreatePopupMenu();
            int hr = menu.QueryContextMenu(hMenu, 0, 1, 0x6FFF, CMF_NORMAL);
            if (hr < 0) { Log.Write($"bg menu: QueryContextMenu hr=0x{hr:X8}"); return; }

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
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_QUIT, "退出 MacDesk (Ctrl+Alt+Q)");

            ForceForeground(ownerHwnd);
            DrainCancelMode(ownerHwnd);
            _activeMenu = menuObj;
            int cmd;
            try { cmd = TrackPopupMenuEx(hMenu, TPM_FLAGS, screenX, screenY, ownerHwnd, IntPtr.Zero); }
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
                default: Invoke(menu, cmd - 1, ownerHwnd); return;
            }
        }
        catch (Exception ex) { Log.Write("bg menu failed: " + ex); }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
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
