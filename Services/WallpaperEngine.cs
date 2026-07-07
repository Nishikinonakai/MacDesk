using System.Runtime.InteropServices;
using System.Text;
using MacDesk.Interop;

namespace MacDesk.Services;

/// <summary>
/// Wallpaper Engine 动态壁纸兼容——"收编"模式（2026-07-07 真机 spike 三命题全过）：
/// WE 的每屏渲染窗 `WPEDesktopDX11Window` 本来住在 Progman 的 WorkerW 里（在 DefView 之下，
/// 被我们不透明的桌面层挡住）。把它跨进程 SetParent 进 DefView、加 WS_EX_TRANSPARENT，
/// z 序垫在呈现层（画图标的 UlwPresenter）与 WPF 输入层之间：
///   UlwPresenter（图标，输入穿透）→ WE 渲染窗（壁纸，输入穿透）→ WPF 窗（收全部输入）。
/// WE 对此无感知、零拷贝原生渲染（spike 实测收编后动画照跑、输入照穿）。
/// 释放时还原父窗口与 exstyle；交接/崩溃时留在 DefView 由继任实例再收编。
/// </summary>
internal static class WallpaperEngine
{
    private const string RenderWindowClass = "WPEDesktopDX11Window";

    /// <summary>找目标显示器的 WE 渲染窗：先搜 Progman 的各 WorkerW（未收编的），
    /// 再搜 DefView（上一任实例收编后没来得及还原的），按屏幕矩形匹配。</summary>
    public static IntPtr FindForMonitor(Native.RECT physical)
    {
        foreach (var w in EnumRenderWindows())
        {
            GetWindowRect(w, out var r);
            // WE 偶有 1px 级别的对齐出入，宽松匹配
            if (Math.Abs(r.Left - physical.Left) <= 2 && Math.Abs(r.Top - physical.Top) <= 2 &&
                Math.Abs(r.Width - physical.Width) <= 4 && Math.Abs(r.Height - physical.Height) <= 4)
                return w;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<IntPtr> EnumRenderWindows()
    {
        if (!DesktopLayer.EnsureDiscovered()) yield break;
        // Progman 下的所有 WorkerW（WE 惯用宿主）
        IntPtr worker = IntPtr.Zero;
        while ((worker = Native.FindWindowEx(DesktopLayer.ProgmanHwnd, worker, "WorkerW", null)) != IntPtr.Zero)
            foreach (var w in ChildrenOfClass(worker, RenderWindowClass))
                yield return w;
        // DefView（我们收编后的位置；继任实例接手时在这找到）
        if (DesktopLayer.DefViewHwnd != IntPtr.Zero)
            foreach (var w in ChildrenOfClass(DesktopLayer.DefViewHwnd, RenderWindowClass))
                yield return w;
    }

    private static IEnumerable<IntPtr> ChildrenOfClass(IntPtr parent, string cls)
    {
        IntPtr c = GetWindow(parent, GW_CHILD);
        while (c != IntPtr.Zero)
        {
            if (ClassNameOf(c) == cls) yield return c;
            c = GetWindow(c, GW_HWNDNEXT);
        }
    }

    private static string ClassNameOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassNameW(h, sb, 64);
        return sb.ToString();
    }

    /// <summary>收编：进 DefView + 输入穿透 + 摆到我们窗口的矩形上。返回还原所需的原状。</summary>
    public static (IntPtr Parent, int ExStyle) Adopt(IntPtr we, Native.RECT parentClientRect)
    {
        var original = (Native.GetParent(we), Native.GetWindowLong(we, Native.GWL_EXSTYLE));
        if (original.Item1 != DesktopLayer.DefViewHwnd)
            Native.SetParent(we, DesktopLayer.DefViewHwnd);
        Native.SetWindowLong(we, Native.GWL_EXSTYLE,
            original.Item2 | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
        Native.MoveWindow(we, parentClientRect.Left, parentClientRect.Top,
            parentClientRect.Width, parentClientRect.Height, true);
        return original;
    }

    /// <summary>释放：还原 exstyle 与父窗口（原父已死就找当前 WorkerW，再不行留在原地）。</summary>
    public static void Release(IntPtr we, IntPtr originalParent, int originalEx)
    {
        if (we == IntPtr.Zero || !Native.IsWindow(we)) return;
        Native.SetWindowLong(we, Native.GWL_EXSTYLE, originalEx);
        IntPtr home = originalParent != IntPtr.Zero && Native.IsWindow(originalParent)
            ? originalParent
            : Native.FindWindowEx(DesktopLayer.ProgmanHwnd, IntPtr.Zero, "WorkerW", null);
        if (home != IntPtr.Zero && home != Native.GetParent(we))
            Native.SetParent(we, home);
    }

    private const uint GW_CHILD = 5, GW_HWNDNEXT = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Native.RECT r);
}
