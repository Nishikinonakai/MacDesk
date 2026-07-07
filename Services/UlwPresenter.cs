using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace MacDesk.Services;

/// <summary>
/// 真透明"三明治"架构的呈现层（UlwSpike 的生产化，spike 三命题真机全过）：
/// 纯 Win32 分层子窗口挂在 SHELLDLL_DefView 下、z 序贴在本显示器 WPF 输入层之下，
/// WS_EX_TRANSPARENT 输入穿透（鼠标/拖放全归上面的 WPF 窗），内容 = MainWindow 离屏
/// 渲染的 premultiplied BGRA 帧经 UpdateLayeredWindow 上屏；alpha=0 区域透出下层
/// （Wallpaper Engine 等动态壁纸的 WorkerW）。
/// DIB 常驻复用（同尺寸帧只 memcpy 不重建），尺寸变化时重建。
/// </summary>
internal sealed class UlwPresenter : IDisposable
{
    public IntPtr Hwnd { get; private set; }

    private int _w, _h;
    private IntPtr _memDc, _dib, _bits, _oldBmp;

    private static ushort _classAtom;
    private static WndProcDelegate? _wndProcKeepAlive; // 防 GC

    private UlwPresenter() { }

    /// <summary>在 parent（DefView）客户区坐标 rect 处创建，z 序放在 above（WPF 输入层）之下。
    /// 失败返回 null（调用方保持不透明路径，功能可用只是没有透传）。</summary>
    public static UlwPresenter? Create(IntPtr parent, IntPtr above, Interop.Native.RECT rect)
    {
        if (!EnsureClass())
        {
            Log.Write("[presenter] RegisterClassEx failed err=" + Marshal.GetLastWin32Error());
            return null;
        }
        var p = new UlwPresenter();
        p.Hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
            ClassName, "", WS_CHILD | WS_VISIBLE,
            rect.Left, rect.Top, rect.Width, rect.Height,
            parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (p.Hwnd == IntPtr.Zero)
        {
            Log.Write("[presenter] CreateWindowEx failed err=" + Marshal.GetLastWin32Error());
            return null;
        }
        SetWindowPos(p.Hwnd, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (!p.EnsureSurface(rect.Width, rect.Height)) { p.Dispose(); return null; }
        return p;
    }

    /// <summary>矩形或 z 序基准变化时同步（CoverAndSync 复查调用；尺寸变了重建 DIB）。</summary>
    public void Sync(IntPtr above, Interop.Native.RECT rect)
    {
        if (Hwnd == IntPtr.Zero) return;
        MoveWindow(Hwnd, rect.Left, rect.Top, rect.Width, rect.Height, false);
        SetWindowPos(Hwnd, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        EnsureSurface(rect.Width, rect.Height);
    }

    /// <summary>推一帧。frame 必须是 Pbgra32（premultiplied，RenderTargetBitmap 的默认输出），
    /// 像素尺寸必须等于窗口尺寸（调用方按 Monitor.Physical 渲染）。</summary>
    public void PushFrame(BitmapSource frame)
    {
        if (Hwnd == IntPtr.Zero || _bits == IntPtr.Zero) return;
        if (frame.PixelWidth != _w || frame.PixelHeight != _h)
        {
            Log.Write($"[presenter] frame {frame.PixelWidth}x{frame.PixelHeight} != surface {_w}x{_h}; skipped");
            return;
        }
        int stride = _w * 4;
        frame.CopyPixels(System.Windows.Int32Rect.Empty, _bits, stride * _h, stride);

        var size = new SIZE { cx = _w, cy = _h };
        var srcPt = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = 0 /*AC_SRC_OVER*/, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
        if (!UpdateLayeredWindow(Hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDc, ref srcPt, 0, ref blend, ULW_ALPHA))
            Log.Write("[presenter] ULW failed err=" + Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        FreeSurface();
        if (Hwnd != IntPtr.Zero) { DestroyWindow(Hwnd); Hwnd = IntPtr.Zero; }
    }

    private bool EnsureSurface(int w, int h)
    {
        if (w < 1 || h < 1) return false;
        if (_bits != IntPtr.Zero && w == _w && h == _h) return true;
        FreeSurface();
        _w = w; _h = h;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            Log.Write("[presenter] CreateDIBSection failed");
            FreeSurface();
            return false;
        }
        _oldBmp = SelectObject(_memDc, _dib);
        return true;
    }

    private void FreeSurface()
    {
        if (_memDc != IntPtr.Zero && _oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp);
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        _memDc = _dib = _bits = _oldBmp = IntPtr.Zero;
        _w = _h = 0;
    }

    private static bool EnsureClass()
    {
        if (_classAtom != 0) return true;
        _wndProcKeepAlive = WndProc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        _classAtom = RegisterClassExW(ref cls);
        return _classAtom != 0;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WS_EX_TRANSPARENT 已保证命中测试跳过本窗；HTTRANSPARENT 是同线程兄弟窗的双保险
        if (msg == 0x0084 /* WM_NCHITTEST */) return new IntPtr(-1) /* HTTRANSPARENT */;
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ── P/Invoke（沿用 UlwSpike 的自包含风格；WNDCLASSEX 字段序是踩过的坑，勿动） ──
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const string ClassName = "MacDeskPresenter";
    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra; // 必须在 hInstance 之前——顺序错 = 静默注册失败 err1407
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, IntPtr dstPt, ref SIZE size,
        IntPtr srcDc, ref POINT srcPt, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
