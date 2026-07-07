using System.Runtime.InteropServices;

namespace MacDesk.Services;

/// <summary>
/// 壁纸透传（真透明）路线的最小验证窗（`MacDesk.exe --ulwtest`）。
///
/// 三个待证命题（WPF 分层子窗口不渲染是既有硬约束，本 spike 走纯 Win32 绕开 WPF 渲染管线）：
/// ① WS_EX_LAYERED | WS_CHILD 子窗口挂 SHELLDLL_DefView 下能否经 UpdateLayeredWindow
///    逐像素 alpha 渲染（Win8+ 文档支持子窗口 ULW，桌面合成场景未验证过）；
/// ② 不透明像素能否收到鼠标输入（点击变色自证）；
/// ③ alpha=0 像素是否点击穿透到下面的兄弟窗口（= 未来图标层浮在动态壁纸上的关键）。
///
/// 全过则主窗口渲染管线可改造为"WPF 离屏渲染 → 按帧推 ULW"，空白区 alpha=1 保点击归属。
/// </summary>
internal static class UlwSpike
{
    private const int W = 400, H = 300, X = 600, Y = 300;
    private static IntPtr _hwnd;
    private static bool _blue;
    private static WndProcDelegate? _wndProcKeepAlive; // 防 GC

    public static void Run()
    {
        if (!Interop.DesktopLayer.EnsureDiscovered("defview"))
        {
            Log.Write("[ulw] no DefView; abort");
            return;
        }
        var parent = Interop.DesktopLayer.DefViewHwnd;
        Log.Write($"[ulw] spike start, parent defview={parent}");

        _wndProcKeepAlive = WndProc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "MacDeskUlwSpike",
        };
        ushort atom = RegisterClassExW(ref cls);
        Log.Write($"[ulw] class atom={atom} err={(atom == 0 ? Marshal.GetLastWin32Error() : 0)}");

        _hwnd = CreateWindowExW(WS_EX_LAYERED, "MacDeskUlwSpike", "",
            WS_CHILD | WS_VISIBLE, X, Y, W, H, parent, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Write($"[ulw] CreateWindowEx FAILED err={Marshal.GetLastWin32Error()}");
            return;
        }
        SetWindowPos(_hwnd, IntPtr.Zero /*HWND_TOP*/, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        Push();
        Log.Write($"[ulw] window created hwnd={_hwnd}, pushed initial frame");

        SetTimer(_hwnd, 1, 120_000, IntPtr.Zero); // 2 分钟自毁，防遗留
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Log.Write("[ulw] spike exit");
    }

    /// <summary>组一帧 premultiplied BGRA 并推给 ULW：全透明底 + 半透明大色块（红/蓝随点击切换）
    /// + 全不透明绿色方块（点击靶）。子窗口 ULW：不动位置只换内容（pptDst 传 NULL）。</summary>
    private static void Push()
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);

        unsafe
        {
            uint* px = (uint*)bits;
            uint tint = _blue ? 0xB40000B4u : 0xB4B40000u; // premult alpha 0xB4 的蓝/红
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    uint c = 0; // alpha=0：期望点击穿透
                    if (x is >= 20 and < 380 && y is >= 20 and < 180) c = tint;
                    if (x is >= 150 and < 250 && y is >= 200 and < 280) c = 0xFF00FF00u; // 点击靶
                    px[y * W + x] = c;
                }
        }

        var size = new SIZE { cx = W, cy = H };
        var srcPt = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = 0 /*AC_SRC_OVER*/, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
        bool ok = UpdateLayeredWindow(_hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
        Log.Write($"[ulw] UpdateLayeredWindow ok={ok} err={(ok ? 0 : Marshal.GetLastWin32Error())} blue={_blue}");

        SelectObject(memDc, old);
        DeleteObject(dib);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0201: // WM_LBUTTONDOWN — ② 命题：不透明像素收到输入
                int cx = (short)((long)lParam & 0xFFFF), cy = (short)(((long)lParam >> 16) & 0xFFFF);
                Log.Write($"[ulw] CLICK received at client ({cx},{cy}) -> toggling color");
                _blue = !_blue;
                Push();
                return IntPtr.Zero;
            case 0x0113: // WM_TIMER — 自毁
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ── P/Invoke（自包含，不污染 Native.cs） ──────────────────
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_EX_LAYERED = 0x80000;
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
    private const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra; // 在 hInstance 之前——顺序错会静默注册失败（err 1407 踩过）
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
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, nuint id, uint ms, IntPtr proc);
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
