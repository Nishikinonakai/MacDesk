using System.Windows.Interop;
using static MacDesk.Interop.Native;

namespace MacDesk;

/// <summary>
/// 隐藏的顶层消息窗口。三个职责：
/// 1. 接收 WM_DISPLAYCHANGE（只发给顶层窗口，主窗口挂成 WS_CHILD 后收不到）
/// 2. 全局热键 Ctrl+Alt+Q 退出
/// 3. 充当原生右键菜单的 owner（TrackPopupMenu 需要可设前台的顶层窗口）
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const int HOTKEY_QUIT = 1;
    private readonly HwndSource _source;

    public IntPtr Handle => _source.Handle;

    public event Action<int, int>? DisplayChanged; // (widthPx, heightPx)
    public event Action? QuitRequested;

    private readonly bool _hotkey;

    public MessageWindow(bool registerHotkey = true)
    {
        _hotkey = registerHotkey;
        var p = new HwndSourceParameters("MacDeskMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0, // 不可见
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        if (_hotkey) RegisterHotKey(Handle, HOTKEY_QUIT, MOD_CONTROL | MOD_ALT, 0x51 /* Q */);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // shell 菜单的 owner-draw 项（Win11 上是全部项）必须转发给 IContextMenu2.HandleMenuMsg，
            // 否则弹出的是一块空白菜单；"新建"子菜单的填充也靠 WM_INITMENUPOPUP 转发
            case 0x0117 /* WM_INITMENUPOPUP */ or 0x002B /* WM_DRAWITEM */
              or 0x002C /* WM_MEASUREITEM */ or 0x0120 /* WM_MENUCHAR */:
                var (fwdHandled, fwdResult) = Services.ShellContextMenu.ForwardMenuMessage(msg, wParam, lParam);
                if (fwdHandled) { handled = true; return fwdResult; }
                break;
            case WM_DISPLAYCHANGE:
                int w = (int)(lParam.ToInt64() & 0xFFFF);
                int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                DisplayChanged?.Invoke(w, h);
                break;
            case WM_DPICHANGED: // 只改缩放比例不改分辨率时走这条
                DisplayChanged?.Invoke(0, 0);
                break;
            case WM_HOTKEY when wParam.ToInt32() == HOTKEY_QUIT:
                QuitRequested?.Invoke();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hotkey) UnregisterHotKey(Handle, HOTKEY_QUIT);
        _source.Dispose();
    }
}
