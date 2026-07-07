using System.Runtime.InteropServices;

namespace MacDesk.Interop;

/// <summary>
/// 系统壁纸信息源（IDesktopWallpaper，Win8+）——壁纸镜像模式的数据源。
/// 真透明不可行（WPF 分层子窗口不渲染，早期实测硬约束），改为把系统当前壁纸按显示器
/// 画成本层背景并跟随变化。动态壁纸（Wallpaper Engine 等）无法镜像，只支持静态图。
/// </summary>
internal static class DesktopWallpaper
{
    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    // DESKTOP_WALLPAPER_POSITION
    public const int PosCenter = 0, PosTile = 1, PosStretch = 2, PosFit = 3, PosFill = 4, PosSpan = 5;

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, out IntPtr wallpaper);
        [PreserveSig] int GetMonitorDevicePathAt(uint monitorIndex, out IntPtr monitorID);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out Native.RECT displayRect);
        void SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        void SetPosition(int position);
        [PreserveSig] int GetPosition(out int position);
        // 之后的 slideshow 方法用不到；COM vtable 按序，省略尾部方法是安全的
    }

    internal sealed record Info(string? ImagePath, int Position, System.Windows.Media.Color BackColor);

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>取指定显示器（按虚拟桌面物理矩形匹配）的壁纸信息；失败返回 null（调用方走注册表兜底）。</summary>
    public static Info? ForMonitor(Native.RECT physical)
    {
        try
        {
            var dw = (IDesktopWallpaper)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidDesktopWallpaper)!)!;

            uint bgr = 0;
            dw.GetBackgroundColor(out bgr);
            var back = System.Windows.Media.Color.FromRgb(
                (byte)(bgr & 0xFF), (byte)((bgr >> 8) & 0xFF), (byte)((bgr >> 16) & 0xFF));

            int pos = PosFill;
            dw.GetPosition(out pos);

            // 找到与本窗口显示器矩形吻合的 monitorID
            string? monId = null;
            if (dw.GetMonitorDevicePathCount(out uint count) == 0)
            {
                for (uint i = 0; i < count; i++)
                {
                    if (dw.GetMonitorDevicePathAt(i, out var pid) != 0) continue;
                    var id = TakeString(pid);
                    if (id == null) continue;
                    if (dw.GetMonitorRECT(id, out var r) != 0) continue; // 未点亮的历史显示器会失败，跳过
                    if (r.Left == physical.Left && r.Top == physical.Top &&
                        r.Right == physical.Right && r.Bottom == physical.Bottom)
                    {
                        monId = id;
                        break;
                    }
                }
            }

            string? path = null;
            if (monId != null && dw.GetWallpaper(monId, out var pw) == 0) path = TakeString(pw);
            if (string.IsNullOrEmpty(path) && dw.GetWallpaper(null, out var pw2) == 0)
                path = TakeString(pw2); // 各屏一致时的整体值兜底

            return new Info(string.IsNullOrEmpty(path) ? null : path, pos, back);
        }
        catch (Exception ex)
        {
            Services.Log.Write("IDesktopWallpaper unavailable: " + ex.Message);
            return null;
        }
    }
}
