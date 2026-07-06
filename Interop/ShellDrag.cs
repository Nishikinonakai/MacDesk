using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Media.Imaging;
using MacDesk.Services;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DragDropEffects = System.Windows.DragDropEffects;

namespace MacDesk.Interop;

/// <summary>
/// Shell 标准 OLE 拖拽（Explorer 同款）：
/// - 拖拽数据对象用 SHCreateDataObject（支持任意 SetData，WPF DataObject 不支持 → 拖拽图像塞不进去）
/// - 拖拽图像走 IDragSourceHelper（DragImageBits 写入数据对象，由 shell 分层窗口跨屏/跨窗口渲染）
/// - 接收端配合 IDropTargetHelper 才会在自己窗口上显示拖拽图像
/// 这一套取代了旧的"手动拖动 + WindowFromPoint 切 OLE"双轨制：图标永远跟着光标，
/// 隔窗、跨屏、长距离都不再丢失视觉。
/// </summary>
internal static class ShellDrag
{
    // ── COM 接口 ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct SHDRAGIMAGE
    {
        public int cx, cy;          // SIZE：位图物理像素
        public Native.POINT ptOffset; // 光标在位图内的偏移（物理像素）
        public IntPtr hbmpDragImage;
        public uint crColorKey;     // 32bpp alpha 位图不用色键 → 0xFFFFFFFF
    }

    [ComImport, Guid("DE5BF786-477A-11d2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref SHDRAGIMAGE pshdi, IDataObject pDataObject);
        void InitializeFromWindow(IntPtr hwnd, ref Native.POINT ppt, IDataObject pDataObject);
    }

    [ComImport, Guid("4657278B-411B-11d2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, IDataObject pDataObject, ref Native.POINT ppt, uint dwEffect);
        void DragLeave();
        void DragOver(ref Native.POINT ppt, uint dwEffect);
        void Drop(IDataObject pDataObject, ref Native.POINT ppt, uint dwEffect);
        void Show(bool fShow);
    }

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(bool fEscapePressed, uint grfKeyState);
        [PreserveSig] int GiveFeedback(uint dwEffect);
    }

    private static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11d2-839A-00C04FD918D0");
    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const uint MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002;
    private const short CF_HDROP = 15;

    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool esc, uint keys)
        {
            if (esc || (keys & MK_RBUTTON) != 0) return DRAGDROP_S_CANCEL;
            if ((keys & MK_LBUTTON) == 0) return DRAGDROP_S_DROP;
            return 0;
        }

        public int GiveFeedback(uint effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr apidl,
        IDataObject? pdtInner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDataObject ppv);

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(IDataObject pDataObj, IDropSource pDropSource, uint dwOKEffects, out uint pdwEffect);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref Native.BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClipboardFormatW(string lpszFormat);

    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>自家拖拽标记格式：完整项列表（含回收站等 CF_HDROP 装不下的虚拟项），\n 分隔 UTF-8。</summary>
    public const string InternalFormat = "MacDesk.DragPaths";

    // ── 数据对象 ─────────────────────────────────────────────

    /// <summary>建一个 shell 数据对象：CF_HDROP（真实文件）+ 自家标记格式（完整项列表含虚拟项）。</summary>
    private static IDataObject CreateFileDataObject(string[] filePaths, string[] allPaths)
    {
        var iid = IID_IDataObject;
        SHCreateDataObject(IntPtr.Zero, 0, IntPtr.Zero, null, ref iid, out var data);

        if (filePaths.Length > 0)
        {
            string joined = string.Join("\0", filePaths) + "\0\0";
            int headerSize = 20; // DROPFILES
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(headerSize + joined.Length * 2));
            IntPtr p = GlobalLock(hGlobal);
            try
            {
                Marshal.WriteInt32(p, 0, headerSize); // pFiles
                Marshal.WriteInt32(p, 4, 0);          // pt.x
                Marshal.WriteInt32(p, 8, 0);          // pt.y
                Marshal.WriteInt32(p, 12, 0);         // fNC
                Marshal.WriteInt32(p, 16, 1);         // fWide
                var chars = joined.ToCharArray();
                Marshal.Copy(chars, 0, p + headerSize, chars.Length);
            }
            finally { GlobalUnlock(hGlobal); }
            SetHGlobal(data, CF_HDROP, hGlobal);
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', allPaths));
        IntPtr hMark = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        IntPtr pm = GlobalLock(hMark);
        try { Marshal.Copy(bytes, 0, pm, bytes.Length); }
        finally { GlobalUnlock(hMark); }
        SetHGlobal(data, (short)RegisterClipboardFormatW(InternalFormat), hMark);
        return data;
    }

    private static void SetHGlobal(IDataObject data, short cfFormat, IntPtr hGlobal)
    {
        var fmt = new FORMATETC
        {
            cfFormat = cfFormat,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = hGlobal };
        data.SetData(ref fmt, ref medium, true); // 数据对象接管 hGlobal
    }

    /// <summary>Pbgra32 位图 → 预乘 alpha 的 32bpp 顶朝下 HBITMAP（拖拽图像要求的格式）。</summary>
    private static IntPtr ToHBitmap(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 顶朝下
                biPlanes = 1,
                biBitCount = 32,
            },
            bmiColors = new uint[256],
        };
        IntPtr hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0 /* DIB_RGB_COLORS */, out IntPtr bits, IntPtr.Zero, 0);
        if (hbm == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
        src.CopyPixels(Int32Rect.Empty, bits, w * h * 4, w * 4);
        return hbm;
    }

    /// <summary>
    /// 发起 shell OLE 拖拽（阻塞到松手/取消）。imagePx 为物理像素位图，hotspotPx 为光标在位图内偏移。
    /// filePaths 进 CF_HDROP（外部目标用）；allPaths 进自家标记格式（本桌面重定位用，可含虚拟项）。
    /// 返回目标接受的效果（None = 取消）。
    /// </summary>
    public static DragDropEffects Start(string[] filePaths, string[] allPaths, BitmapSource imagePx, Native.POINT hotspotPx)
    {
        var data = CreateFileDataObject(filePaths, allPaths);
        try
        {
            var helperObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_DragDropHelper)!)!;
            var shdi = new SHDRAGIMAGE
            {
                cx = imagePx.PixelWidth,
                cy = imagePx.PixelHeight,
                ptOffset = hotspotPx,
                hbmpDragImage = ToHBitmap(imagePx), // 成功时归 helper 释放
                crColorKey = 0xFFFFFFFF,
            };
            try { ((IDragSourceHelper)helperObj).InitializeFromBitmap(ref shdi, data); }
            catch (Exception ex)
            {
                Native.DeleteObject(shdi.hbmpDragImage);
                Log.Write("drag image init failed (continuing without): " + ex.Message);
            }
        }
        catch (Exception ex) { Log.Write("DragDropHelper unavailable: " + ex.Message); }

        int hr = DoDragDrop(data, new DropSource(),
            (uint)(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link), out uint effect);
        return hr == DRAGDROP_S_DROP ? (DragDropEffects)effect : DragDropEffects.None;
    }

    /// <summary>接收端的拖拽图像助手（每窗口一个实例，DragEnter/Over/Leave/Drop 全程调用才有图像）。</summary>
    public static IDropTargetHelper? CreateDropTargetHelper()
    {
        try { return (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_DragDropHelper)!)!; }
        catch { return null; }
    }

    /// <summary>WPF DragEventArgs 里的数据对象取回原生 COM 接口（喂给 IDropTargetHelper）。</summary>
    public static IDataObject? ComDataObject(object wpfData) => wpfData as IDataObject;
}
