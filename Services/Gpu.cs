using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MacDesk.Services;

/// <summary>显示适配器侦察（issue #1）。老世代 Intel 核显（HD/UHD 6xx、Iris Plus/Pro 等）
/// 的原生 D3D9 驱动（31.0.101.x legacy 分支，已停功能维护）在 WPF 硬件合成路径把壁纸镜像
/// 亮部烧成噪点/白块——反馈者实锤 1:1 零缩放照烧、最新驱动无效，app 侧无参数可绕，只能
/// 整进程软渲避开 D3D9 硬件路径。Gen12/Xe/Arc 起（Iris Xe、UHD 7xx、Arc、Core Ultra）
/// D3D9 走 D3D9On12 翻译层（D3D12 实现），实测免疫（Core Ultra 反证），保持硬件渲染。</summary>
internal static class Gpu
{
    /// <summary>任一在用适配器是老世代 Intel 核显即 true（name 带命中名，日志用）。
    /// 宁可错杀：以静态为主的桌面层软渲代价很小，且设置里可「强制硬件」拨回。</summary>
    public static bool HasLegacyIntelAdapter(out string name)
    {
        foreach (var (devName, devId) in ActiveAdapters())
            if (IsLegacyIntel(devName, devId)) { name = devName; return true; }
        name = "";
        return false;
    }

    /// <summary>桌面在用的适配器名（去重），启动日志/渲染类 issue 分诊用。</summary>
    public static string ActiveAdapterSummary()
    {
        var names = ActiveAdapters().Select(a => a.Name).Distinct().ToList();
        return names.Count > 0 ? string.Join("; ", names) : "(none detected)";
    }

    private static bool IsLegacyIntel(string name, string deviceId)
    {
        if (!deviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return false;
        // Gen12+/独显（D3D9On12，免疫）放行：Arc、Iris Xe/Xe MAX、Core Ultra 的 "Intel Graphics"
        if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Xe", StringComparison.OrdinalIgnoreCase)) return false;
        // HD/UHD 家族：UHD 7xx 是 Gen12 桌面（放行），带其他数字（630/620/4600…）或不带数字
        // （Ice/Comet Lake 的 "UHD Graphics"、"HD Graphics Family"）都算老世代
        var m = Regex.Match(name, @"\bU?HD Graphics\b(?:\s+(\d+))?", RegexOptions.IgnoreCase);
        if (m.Success)
            return !(m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out int n) && n is >= 700 and <= 799);
        return name.Contains("Iris", StringComparison.OrdinalIgnoreCase); // Iris/Pro/Plus（Gen7.5–11；Xe 已放行）
    }

    private static List<(string Name, string Id)> ActiveAdapters()
    {
        var list = new List<(string, string)>();
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 &&
                    (dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) == 0)
                    list.Add((dd.DeviceString, dd.DeviceID));
                dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            }
        }
        catch { } // 侦察失败 = 空列表 → 判定不出老核显，维持硬件渲染默认
        return list;
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

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
}
