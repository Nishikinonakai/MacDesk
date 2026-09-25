using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MacDesk.Services;

/// <summary>Mounted removable media and USB disks. USB hard disks often report DriveType.Fixed.</summary>
internal static class ExternalDrives
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[] input, uint inputSize, byte[] output, uint outputSize,
        out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint,
        StringBuilder volumeName, uint length);

    public static string LayoutName(string root)
    {
        var name = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPointW(root, name, (uint)name.Capacity)
            ? "Drive:" + name.ToString().TrimEnd('\\')
            : "Drive:" + root.TrimEnd('\\');
    }

    public static IReadOnlyList<DesktopEntry> Enumerate()
    {
        var result = new List<DesktopEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                if (drive.DriveType == DriveType.Fixed && !IsUsbOrCard(drive.Name)) continue;
                string label = drive.VolumeLabel;
                string letter = drive.Name.TrimEnd('\\');
                result.Add(new DesktopEntry(drive.Name,
                    string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})",
                    LayoutName(drive.Name)));
            }
            catch (IOException) { } // Media can disappear between enumeration and inspection.
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private static bool IsUsbOrCard(string root)
    {
        using var handle = CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        // STORAGE_PROPERTY_QUERY: StorageDeviceProperty (0), PropertyStandardQuery (0).
        var output = new byte[64];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, new byte[12], 12,
                output, (uint)output.Length, out uint returned, IntPtr.Zero) || returned < 32)
            return false;
        // STORAGE_DEVICE_DESCRIPTOR.BusType is at byte offset 28.
        uint bus = BitConverter.ToUInt32(output, 28);
        return bus is 7 /* USB */ or 12 /* SD */ or 13 /* MMC */;
    }
}
