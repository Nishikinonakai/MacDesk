using static MacDesk.Interop.Native;

namespace MacDesk.Interop;

/// <summary>
/// 把窗口挂到桌面层：壁纸之上、所有应用窗口之下、Win+D 不隐藏。
/// 多显示器：DefView 横跨整个虚拟桌面（26200 实测 (0,0)-(全物理并集)），
/// 每显示器一个子窗口都挂同一个父窗口，各自盖住自己的显示器矩形。
/// 兼容两代桌面结构（ManagedShell 同款双路径）：
///  - Win11 24H2+ / 经典结构：SHELLDLL_DefView 直接在 Progman 下 → 挂 Progman
///  - Win8~Win11 23H2 开壁纸幻灯片等场景：DefView 被移进某个 WorkerW → 挂那个 WorkerW
/// </summary>
internal static class DesktopLayer
{
    public static IntPtr ProgmanHwnd { get; private set; }
    public static IntPtr DefViewHwnd { get; private set; }
    public static IntPtr ListViewHwnd { get; private set; }
    public static IntPtr ParentHwnd { get; private set; }

    /// <summary>发现桌面层父窗口（进程内一次性；失败返回 false 可重试）。
    /// defview 模式下找不到 SHELLDLL_DefView 就返回 false **不缓存**：开机自启（尤其计划任务
    /// 的登录即启）会撞上"Progman 已出生、DefView 还没建"的窗口期，此时退而求其次挂 Progman
    /// 会把这个错父窗口永久缓存进来——图标层不在桌面视图里（Win11 26200 上根本不渲染）、
    /// 原生图标也藏不掉，且 _attached 已置位再也不会重试。调用方（AttemptAttach）耐心重试即可。</summary>
    public static bool EnsureDiscovered(string parentMode = "defview")
    {
        if (ParentHwnd != IntPtr.Zero && IsWindow(ParentHwnd)) return true;

        ProgmanHwnd = FindWindow("Progman", null);
        if (ProgmanHwnd == IntPtr.Zero) return false;

        var defView = FindWindowEx(ProgmanHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // 老结构才需要经典技巧：让 Progman 分离出 WorkerW
            SendMessageTimeout(ProgmanHwnd, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
            defView = FindWindowEx(ProgmanHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        IntPtr parent;
        if (defView != IntPtr.Zero)
        {
            parent = ProgmanHwnd;
        }
        else
        {
            // 枚举顶层 WorkerW，找包含 SHELLDLL_DefView 的那个
            IntPtr worker = IntPtr.Zero, dv = IntPtr.Zero;
            do
            {
                worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
                dv = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            } while (dv == IntPtr.Zero && worker != IntPtr.Zero);

            if (dv != IntPtr.Zero) { parent = worker; defView = dv; }
            else parent = ProgmanHwnd; // 兜底
        }

        switch (parentMode)
        {
            case "defview" when defView != IntPtr.Zero:
                parent = defView;
                break;
            case "defview":
                return false; // 桌面视图还没建好：不缓存，等下一发重试
            case "workerw":
                var w2 = FindWindowEx(ProgmanHwnd, IntPtr.Zero, "WorkerW", null);
                if (w2 != IntPtr.Zero) parent = w2;
                break;
        }

        DefViewHwnd = defView;
        if (defView != IntPtr.Zero)
            ListViewHwnd = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        ParentHwnd = parent;
        return true;
    }

    /// <summary>启动最早期就把原生图标藏掉（不挂窗口）：等 WPF 起窗口+首帧渲染再藏，
    /// 真机实测有 ~1s 的"原生图标闪一下"窗口期（用户反馈）。找不到桌面就返回 false，
    /// 正常挂载路径（AttemptAttach）会再藏一次。</summary>
    public static bool TryHideNativeIconsEarly(string parentMode = "defview")
    {
        try
        {
            if (!EnsureDiscovered(parentMode)) return false;
            if (!NativeIconsVisible) return true; // 已经是藏的（持久状态生效 = 开机压根没画）
            SetNativeIconsVisible(false);
            return !NativeIconsVisible;
        }
        catch { return false; }
    }

    /// <summary>把一个（已完成首帧渲染的）窗口挂进桌面层。多窗口各自调用。</summary>
    public static bool AttachWindow(IntPtr hwnd, bool setChild = true)
    {
        if (ParentHwnd == IntPtr.Zero) return false;
        if (setChild)
        {
            // 必须在窗口完成首帧渲染之后再改（Show 中途改会掐死 WPF 渲染管线）
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, (style | WS_CHILD) & ~WS_POPUP);
        }
        if (SetParent(hwnd, ParentHwnd) == IntPtr.Zero) return false;
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return true;
    }

    /// <summary>把窗口移动到指定物理矩形（虚拟桌面坐标 → 父窗口客户区坐标）。
    /// 返回父客户区坐标矩形（给 ForceCoverHook 钳制用）。</summary>
    public static RECT CoverRect(IntPtr hwnd, RECT physical)
    {
        var rect = physical;
        MapWindowPoints(IntPtr.Zero, ParentHwnd, ref rect, 2);
        MoveWindow(hwnd, rect.Left, rect.Top, rect.Width, rect.Height, true);
        Services.Log.Write($"cover: phys=({physical.Left},{physical.Top},{physical.Width}x{physical.Height}) -> parent-client=({rect.Left},{rect.Top},{rect.Width}x{rect.Height})");
        return rect;
    }

    /// <summary>原生图标列表句柄（现查兜底）：发现桌面时 DefView 可能刚建好、SysListView32
    /// 还没出生（早藏路径把发现时机提前后更容易撞上），那次拿到的 0 会被永久缓存 =
    /// 整个会话都藏不掉原生图标。每次用之前复核一遍。</summary>
    private static IntPtr ResolveListView()
    {
        if (ListViewHwnd != IntPtr.Zero && IsWindow(ListViewHwnd)) return ListViewHwnd;
        if (DefViewHwnd != IntPtr.Zero && IsWindow(DefViewHwnd))
            ListViewHwnd = FindWindowEx(DefViewHwnd, IntPtr.Zero, "SysListView32", null);
        return ListViewHwnd;
    }

    /// <summary>只藏 SysListView32（原生图标列表）——DefView 不能藏，我们自己挂在它下面。</summary>
    public static void SetNativeIconsVisible(bool visible)
    {
        var lv = ResolveListView();
        if (lv != IntPtr.Zero && IsWindow(lv))
            ShowWindow(lv, visible ? SW_SHOW : SW_HIDE);
        if (visible && DefViewHwnd != IntPtr.Zero && IsWindow(DefViewHwnd) && !IsWindowVisible(DefViewHwnd))
            ShowWindow(DefViewHwnd, SW_SHOW);
    }

    public static bool NativeIconsVisible
    {
        get { var lv = ResolveListView(); return lv != IntPtr.Zero && IsWindowVisible(lv); }
    }
}
