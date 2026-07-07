using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDesk.Interop;

namespace MacDesk.Services;

/// <summary>IShellItemImageFactory 取高分辨率图标/缩略图（含 alpha）。
/// 按（扩展名, 尺寸）共享缓存：千文件压测实锤内存大头是每文件 ~0.6MB 的独立位图
/// （1000 个 .txt 各存一份同样的图标）。共享路径强制 SIIGBF_ICONONLY——类型图标由
/// 扩展名决定，绝不会把 A 文件的内容缩略图错共享给 B；有缩略图价值的类型（图片/视频/
/// PDF）和每文件图标类型（exe/lnk/ico…）+ 目录 + 虚拟项保持逐文件加载不缓存。</summary>
internal static class IconLoader
{
    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_ICONONLY = 0x04;

    /// <summary>图标随文件本体变化的类型：共享会张冠李戴。</summary>
    private static readonly HashSet<string> PerFileIcon = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".lnk", ".url", ".ico", ".cur", ".ani", ".scr", ".appref-ms", ".msi", ".dll" };

    /// <summary>保留内容缩略图的类型（Explorer 同款观感）：逐文件加载不缓存。</summary>
    private static readonly HashSet<string> ThumbnailExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".svg",
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v", ".pdf",
    };

    private static readonly Dictionary<string, ImageSource?> _shared = new(StringComparer.OrdinalIgnoreCase);

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
        string ext = System.IO.Path.GetExtension(path);
        bool shareable = ext.Length > 1
            && !PerFileIcon.Contains(ext) && !ThumbnailExts.Contains(ext)
            && !path.StartsWith("::") && !System.IO.Directory.Exists(path);
        if (!shareable) return LoadUncached(path, sizePx, SIIGBF_BIGGERSIZEOK);

        string key = $"{ext}|{sizePx}";
        lock (_shared)
            if (_shared.TryGetValue(key, out var cached)) return cached;
        var src = LoadUncached(path, sizePx, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY);
        if (src != null)
            lock (_shared) _shared[key] = src;
        return src;
    }

    private static ImageSource? LoadUncached(string path, int sizePx, int flags)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o);
            var factory = (IShellItemImageFactory)o;
            if (factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, flags, out hbm) != 0 || hbm == IntPtr.Zero)
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
