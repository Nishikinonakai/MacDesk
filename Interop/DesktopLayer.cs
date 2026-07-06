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

    /// <summary>发现桌面层父窗口（进程内一次性；失败返回 false 可重试）。</summary>
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

    /// <summary>只藏 SysListView32（原生图标列表）——DefView 不能藏，我们自己挂在它下面。</summary>
    public static void SetNativeIconsVisible(bool visible)
    {
        if (ListViewHwnd != IntPtr.Zero && IsWindow(ListViewHwnd))
            ShowWindow(ListViewHwnd, visible ? SW_SHOW : SW_HIDE);
        if (visible && DefViewHwnd != IntPtr.Zero && IsWindow(DefViewHwnd) && !IsWindowVisible(DefViewHwnd))
            ShowWindow(DefViewHwnd, SW_SHOW);
    }

    public static bool NativeIconsVisible =>
        ListViewHwnd != IntPtr.Zero && IsWindowVisible(ListViewHwnd);
}
