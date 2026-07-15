using System.Runtime.InteropServices;

namespace MacDesk.Interop;

/// <summary>一块显示器的静态信息（进程 PMv2，坐标全物理像素）。显示配置变化会触发自我重启，
/// 所以枚举结果在窗口生命周期内有效。</summary>
internal sealed record MonitorInfo(
    string Key,          // 稳定标识（EDID 厂商+型号码，如 "GSM5B08"；同型号多台加 #2 后缀）
    IntPtr HMonitor,
    Native.RECT Physical, // 显示器物理像素矩形（虚拟桌面坐标系）
    uint Dpi,             // 有效 DPI（96=100%）
    bool IsPrimary,
    string Device);       // \\.\DISPLAY1（调试用，不做持久化 key）

internal static class Monitors
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Native.RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint iDevNum, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);

    /// <summary>显示器当前刷新率（Hz）。查不到 / 报 0-1（硬件默认）时按 60 处理。
    /// 动态壁纸帧泵用它做节流下限（高刷屏别被 60fps 卡死——用户反馈驱动）。</summary>
    public static int RefreshRate(string device)
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(device, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmDisplayFrequency > 1)
                return (int)dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }

    /// <summary>本窗口所在显示器的工作区四边内缩（物理 px）：rcWork 相对 rcMonitor。
    /// 任务栏的所有形态——小图标/隐藏/自动隐藏/四边停靠——全被这一个矩形描述，
    /// 布局按它避让，不猜任务栏多高。现查不缓存：任务栏挪位/改高不触发显示器事件。</summary>
    public static (int Left, int Top, int Right, int Bottom)? WorkInsets(IntPtr hwnd)
    {
        var h = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (h == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoW(h, ref mi)) return null;
        return (mi.rcWork.Left - mi.rcMonitor.Left, mi.rcWork.Top - mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcWork.Right, mi.rcMonitor.Bottom - mi.rcWork.Bottom);
    }

    /// <summary>枚举当前接入的显示器。主显示器排第一。</summary>
    public static List<MonitorInfo> GetAll()
    {
        var raw = new List<(IntPtr h, MONITORINFOEX mi)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(h, ref mi)) raw.Add((h, mi));
            return true;
        }, IntPtr.Zero);

        var list = new List<MonitorInfo>();
        var seen = new Dictionary<string, int>();
        foreach (var (h, mi) in raw)
        {
            uint dpi = 96;
            if (Native.GetDpiForMonitor(h, 0, out uint dx, out _) == 0) dpi = dx;
            string key = EdidKey(mi.szDevice) ?? mi.szDevice.TrimStart('\\', '.');
            if (seen.TryGetValue(key, out int n)) { seen[key] = n + 1; key = $"{key}#{n + 1}"; }
            else seen[key] = 1;
            list.Add(new MonitorInfo(key, h, mi.rcMonitor, dpi, (mi.dwFlags & 1) != 0, mi.szDevice));
        }
        // 主显示器排第一（新图标/孤儿图标的默认归属）
        return list.OrderByDescending(m => m.IsPrimary).ToList();
    }

    private const uint DISPLAY_DEVICE_ACTIVE = 0x1;

    /// <summary>EDID 厂商+型号码（DeviceID 形如 MONITOR\GSM5B08\{GUID}\0001 → GSM5B08）。
    /// 与线序/接口无关，同一台物理显示器换口不换 key。
    /// 坑（26200 实测）：适配器下可能挂多个 monitor 子设备（连线未点亮的也在列），
    /// child0 不一定是活动的——必须选 DISPLAY_DEVICE_ACTIVE 的那个。</summary>
    private static string? EdidKey(string adapterDevice)
    {
        string? fallback = null;
        for (uint i = 0; i < 8; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDevice, i, ref dd, 0)) break;
            var parts = dd.DeviceID.Split('\\');
            string? key = parts.Length >= 2 && parts[1].Length > 0 ? parts[1] : null;
            if (key == null) continue;
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0) return key;
            fallback ??= key;
        }
        return fallback;
    }
}
