using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDesk.Interop;

namespace MacDesk.Services;

/// <summary>IShellItemImageFactory 取高分辨率图标/缩略图（含 alpha）。</summary>
internal static class IconLoader
{
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    public static ImageSource? Load(string path, int sizePx)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o);
            var factory = (IShellItemImageFactory)o;
            if (factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            return HBitmapToSource(hbm);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
        }
    }

    /// <summary>GetDIBits 手工转 BGRA，保住 CreateBitmapSourceFromHBitmap 会丢的 alpha 通道。</summary>
    private static BitmapSource? HBitmapToSource(IntPtr hbm)
    {
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = bm.bmWidth,
                biHeight = -bm.bmHeight, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
            bmiColors = new uint[256],
        };

        int stride = bm.bmWidth * 4;
        var bits = new byte[stride * bm.bmHeight];
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hbm, 0, (uint)bm.bmHeight, bits, ref bi, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }

        // 部分图标源给的是直通 alpha（straight），当预乘（Pbgra32）用会在半透明边缘泛白圈
        // （机主实锤 Clash Verge 白边）。预乘图里任一色道不可能大于 alpha——据此检测并转换。
        bool straight = false;
        for (int i = 0; i < bits.Length; i += 4)
        {
            byte a = bits[i + 3];
            if (bits[i] > a || bits[i + 1] > a || bits[i + 2] > a) { straight = true; break; }
        }
        if (straight)
        {
            for (int i = 0; i < bits.Length; i += 4)
            {
                byte a = bits[i + 3];
                bits[i] = (byte)(bits[i] * a / 255);
                bits[i + 1] = (byte)(bits[i + 1] * a / 255);
                bits[i + 2] = (byte)(bits[i + 2] * a / 255);
            }
        }

        var src = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, bits, stride);
        src.Freeze();
        return src;
    }
}
