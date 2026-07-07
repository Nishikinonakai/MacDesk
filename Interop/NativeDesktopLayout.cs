using System.Runtime.InteropServices;
using MacDesk.Services;

namespace MacDesk.Interop;

/// <summary>
/// 读取原生桌面 ListView 的图标位置（OOBE 首启导入：新装机器不打乱用户原有摆放）。
/// 跨进程读法与方案 A（desktop-icon-backup-manager）同款：VirtualAllocEx 在 Explorer
/// 里租一块内存，LVM_GETITEMPOSITION / LVM_GETITEMTEXTW 往返。
/// </summary>
internal static class NativeDesktopLayout
{
    private const uint LVM_GETITEMCOUNT = 0x1004, LVM_GETITEMPOSITION = 0x1010, LVM_GETITEMTEXTW = 0x1073;
    private const uint PROCESS_VM_OPERATION = 0x08, PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEM64
    {
        public uint mask;
        public int iItem, iSubItem;
        public uint state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage;
        public IntPtr lParam;
        public int iIndent, iGroupId;
        public uint cColumns;
        public IntPtr puColumns, piColFmt;
        public int iGroup;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal sealed record NativeIcon(string Name, int ScreenX, int ScreenY); // 图标区左上角（物理屏幕坐标）

    public static List<NativeIcon> Read()
    {
        var result = new List<NativeIcon>();
        var lv = DesktopLayer.ListViewHwnd;
        if (lv == IntPtr.Zero) return result;

        int count = (int)SendMessageW(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0 || count > 2000) return result;

        Native.GetWindowThreadProcessId(lv, out uint pid);
        IntPtr proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (proc == IntPtr.Zero) { Log.Write("native layout: OpenProcess denied"); return result; }
        IntPtr remote = IntPtr.Zero;
        try
        {
            const int textCap = 520; // 字符数
            int itemSize = Marshal.SizeOf<LVITEM64>();
            remote = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)(itemSize + textCap * 2 + 64),
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return result;
            IntPtr remoteText = remote + itemSize + 16;

            for (int i = 0; i < count; i++)
            {
                // 位置（ListView 客户区坐标）
                if (SendMessageW(lv, LVM_GETITEMPOSITION, (IntPtr)i, remote) == IntPtr.Zero) continue;
                var ptBuf = new byte[8];
                if (!ReadProcessMemory(proc, remote, ptBuf, (IntPtr)8, out _)) continue;
                int cx = BitConverter.ToInt32(ptBuf, 0), cy = BitConverter.ToInt32(ptBuf, 4);

                // 文本
                var item = new LVITEM64 { iSubItem = 0, pszText = remoteText, cchTextMax = textCap };
                var itemBuf = new byte[itemSize];
                var h = GCHandle.Alloc(itemBuf, GCHandleType.Pinned);
                try
                {
                    Marshal.StructureToPtr(item, h.AddrOfPinnedObject(), false);
                }
                finally { h.Free(); }
                if (!WriteProcessMemory(proc, remote, itemBuf, (IntPtr)itemSize, out _)) continue;
                int len = (int)SendMessageW(lv, LVM_GETITEMTEXTW, (IntPtr)i, remote);
                if (len <= 0) continue;
                var textBuf = new byte[Math.Min(len, textCap - 1) * 2];
                if (!ReadProcessMemory(proc, remoteText, textBuf, (IntPtr)textBuf.Length, out _)) continue;
                string name = System.Text.Encoding.Unicode.GetString(textBuf);

                // 客户区 → 物理屏幕坐标（本进程 PMv2，MapWindowPoints 给真值）
                var rc = new Native.RECT { Left = cx, Top = cy };
                Native.MapWindowPoints(lv, IntPtr.Zero, ref rc, 1);
                result.Add(new NativeIcon(name, rc.Left, rc.Top));
            }
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(proc, remote, IntPtr.Zero, MEM_RELEASE);
            CloseHandle(proc);
        }
        return result;
    }

    /// <summary>
    /// 把原生桌面摆放转成规范锚距写入布局档。OOBE 首启询问后调用，也供设置里
    /// "导入原生桌面布局"手动触发（会覆盖同名图标的现有位置）。按显示名匹配（含回收站）。
    /// </summary>
    public static int Import(List<NativeIcon> native, List<MonitorInfo> monitors, LayoutStore layout, IReadOnlyList<DesktopEntry> entries)
    {
        if (native.Count == 0) { Log.Write("native layout import: nothing to import"); return 0; }

        // 图标区左上角 → 中心：native 网格间距的一半（LOGPIXELS 相关，读系统值兜底 75×100）
        int spacingX = Native.GetSystemMetrics(38), spacingY = Native.GetSystemMetrics(39); // SM_CX/CYICONSPACING
        if (spacingX < 32 || spacingX > 300) spacingX = 75;
        if (spacingY < 32 || spacingY > 300) spacingY = 100;

        var primary = monitors[0];
        int imported = 0;
        foreach (var ni in native)
        {
            var entry = entries.FirstOrDefault(en =>
                string.Equals(en.DisplayName, ni.Name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;

            double px = ni.ScreenX + spacingX / 2.0, py = ni.ScreenY + spacingY / 2.0;
            var mon = monitors.FirstOrDefault(m =>
                px >= m.Physical.Left && px < m.Physical.Right &&
                py >= m.Physical.Top && py < m.Physical.Bottom) ?? primary;

            double scale = mon.Dpi / 96.0;
            double w = mon.Physical.Width / scale, hgt = mon.Physical.Height / scale;
            double cx = (px - mon.Physical.Left) / scale, cy = (py - mon.Physical.Top) / scale;
            bool fromBottom = cy > hgt * 0.6; // 与 PosToCanon 同阈值
            layout.Set(mon.Key, System.IO.Path.GetFileName(entry.Path),
                new CanonPos(w - cx, fromBottom ? hgt - cy : cy, fromBottom));
            imported++;
        }
        layout.Save();
        Log.Write($"native layout import: {imported}/{native.Count} icon positions adopted");
        return imported;
    }
}
