using System.Runtime.InteropServices;
using static MacDesk.Interop.Native;

namespace MacDesk.Services;

/// <summary>
/// 主进程同线程弹菜单（序列化路径的显示端）。
///
/// 为什么风暴杀不到它：主窗口 SetParent 挂在 DefView 下，跨进程 SetParent 的副作用是
/// 与 Explorer 桌面线程共享同一输入队列——所以 TrackPopupMenu 拿得到输入（键盘能力
/// 同理成立）；而"关菜单后前台分两站异步归还（主窗口 → Progman）"两站都落在这个
/// 共享队列内、其中一站还是 owner 自己，不构成对 owner 线程的失活。剩余的杂散
/// WM_CANCELMODE 由 MainWindow 钩子在 MenuOpen 期间吞掉（真点击外部的关闭走菜单
/// 自己的捕获逻辑，不经 WM_CANCELMODE，吞它不影响正常关闭）。
/// </summary>
internal static class NativeMenuPresenter
{
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_NOANIMATION = 0x4000;

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr lprc, IntPtr hrgn, uint flags);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapseMs, IntPtr proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    /// <summary>当前打开菜单的重建资源（owner WndProc 的 measure/draw 回放用）。仅 UI 线程碰。</summary>
    public static MenuSnapshot.Built? Current { get; private set; }

    /// <summary>菜单开着：MainWindow 钩子在此期间吞 WM_CANCELMODE。</summary>
    public static bool MenuOpen => Current != null;

    /// <summary>UI 线程上重建并弹出菜单，返回选中命令 id（0 = 取消）。</summary>
    public static uint Track(IntPtr ownerHwnd, List<MenuSnapshot.Item> items, int x, int y)
    {
        // 桌面链请到前台 + 键盘焦点给自己：菜单模态循环在本线程，焦点在本队列的我们
        // 窗口上时方向键/Esc 才会进菜单（真机实测：不 SetFocus 则 Esc 关不掉菜单）。
        // 旧 host 路径禁 SetFocus 是因为 WPF 拿焦点会异步夺前台杀掉别进程的菜单；
        // 同线程菜单里这个激活落在自己身上，无杀伤力。
        SetForegroundWindow(GetAncestor(ownerHwnd, GA_ROOT));
        SetFocus(ownerHwnd);
        using var built = MenuSnapshot.Build(items);
        Current = built;
        try
        {
            int cmd = TrackPopupMenuEx(built.Handle, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NOANIMATION,
                x, y, ownerHwnd, IntPtr.Zero);
            return (uint)cmd;
        }
        finally { Current = null; }
    }

    // ── 子菜单白块修复 ────────────────────────────────────────
    // TPM_NOANIMATION 只管顶层弹出；子菜单自带淡入动画，而非前台线程的菜单动画不渲染，
    // 表现为白块、划过哪行显影哪行（真机复现，与深夜批次的顶层白块同病）。
    // 修法：owner 收到 WM_INITMENUPOPUP 后启动短定时器，把所有可见菜单窗口（#32768）
    // 连续几拍强制 RedrawWindow——动画帧欠的账由我们直接重绘补上。

    private static readonly UIntPtr RedrawTimerId = (UIntPtr)0x4D52; // 'MR'
    private static int _redrawTicks;

    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80,
                       RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;

    /// <summary>owner 的 WM_INITMENUPOPUP（仅菜单打开期间调用）。</summary>
    public static void OnInitMenuPopup(IntPtr ownerHwnd)
    {
        if (!MenuOpen) return;
        _redrawTicks = 0;
        SetTimer(ownerHwnd, RedrawTimerId, 50, IntPtr.Zero);
    }

    /// <summary>owner 的 WM_TIMER。认领返回 true。</summary>
    public static bool OnTimer(IntPtr ownerHwnd, IntPtr timerId)
    {
        if ((ulong)timerId != (ulong)RedrawTimerId) return false;
        RedrawMenuWindows();
        if (++_redrawTicks >= 6 || !MenuOpen) KillTimer(ownerHwnd, RedrawTimerId);
        return true;
    }

    private static void RedrawMenuWindows()
    {
        IntPtr w = IntPtr.Zero;
        while ((w = FindWindowExW(IntPtr.Zero, w, "#32768", null)) != IntPtr.Zero)
            if (IsWindowVisible(w))
                RedrawWindow(w, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_UPDATENOW | RDW_ALLCHILDREN);
    }

    // ── MacDesk 自定义项 / 降级菜单（主进程本地 id，与 host 的 shell id 空间无关） ──

    private const uint ID_ARRANGE = 0x7001, ID_UNDO = 0x7002, ID_TOGGLE = 0x7003, ID_QUIT = 0x7004,
                       ID_AUTOSTART = 0x7005, ID_SORT_NAME = 0x7006, ID_SORT_DATE = 0x7007,
                       ID_SORT_SIZE = 0x7008, ID_SORT_KIND = 0x7009, ID_FREE = 0x700A;
    private const uint ID_D_OPEN = 0x7101, ID_D_OPENWITH = 0x7102, ID_D_CUT = 0x7103, ID_D_COPY = 0x7104,
                       ID_D_RENAME = 0x7105, ID_D_DELETE = 0x7106, ID_D_PROPS = 0x7107;

    private static MenuSnapshot.Item Sep() => new() { Sep = true };
    private static MenuSnapshot.Item Cmd(uint id, string text, bool check = false) =>
        new() { Id = id, Text = text, Checked = check };

    /// <summary>背景菜单尾部的 MacDesk 自定义项（追加到 host 序列化来的 shell 项之后）。</summary>
    public static List<MenuSnapshot.Item> CustomBackgroundItems() => new()
    {
        Sep(),
        Cmd(ID_ARRANGE, "按 mac 式网格整理"),
        new MenuSnapshot.Item
        {
            Text = "排序方式",
            Children = new List<MenuSnapshot.Item>
            {
                Cmd(ID_SORT_NAME, "名称"), Cmd(ID_SORT_DATE, "修改日期"),
                Cmd(ID_SORT_SIZE, "大小"), Cmd(ID_SORT_KIND, "类型"),
            },
        },
        Cmd(ID_UNDO, "撤销上次整理"),
        Cmd(ID_FREE, "自由摆放（不吸附网格）", Settings.Load().FreePlacement),
        Cmd(ID_TOGGLE, "显示/隐藏原生图标"),
        Cmd(ID_AUTOSTART, "开机自启", Autostart.IsEnabled()),
        Cmd(ID_QUIT, "退出 MacDesk (Ctrl+Alt+Q)"),
    };

    /// <summary>降级文件菜单（探针判定该类型原生菜单必崩时）。动词与旧 host 版一致。</summary>
    public static List<MenuSnapshot.Item> DegradedFileItems(string[] paths)
    {
        var items = new List<MenuSnapshot.Item> { Cmd(ID_D_OPEN, "打开") };
        if (paths.Length == 1 && !paths[0].StartsWith("::")) items.Add(Cmd(ID_D_OPENWITH, "打开方式…"));
        items.Add(Sep());
        items.Add(Cmd(ID_D_CUT, "剪切"));
        items.Add(Cmd(ID_D_COPY, "复制"));
        items.Add(Sep());
        if (paths.Length == 1) items.Add(Cmd(ID_D_RENAME, "重命名"));
        items.Add(Cmd(ID_D_DELETE, "删除"));
        items.Add(Sep());
        items.Add(Cmd(ID_D_PROPS, "属性"));
        return items;
    }

    /// <summary>本地命令（0x7xxx）分发。复用 CommandChannel 的 Listen 注册（进程内 Signal 闭环），
    /// 返回 true = 已处理（shell 范围外，不回传 host）。</summary>
    public static bool DispatchLocal(uint cmd, string[] paths)
    {
        switch (cmd)
        {
            case ID_ARRANGE: CommandChannel.Signal("Arrange"); return true;
            case ID_UNDO: CommandChannel.Signal("Undo"); return true;
            case ID_TOGGLE: CommandChannel.Signal("ToggleNative"); return true;
            case ID_AUTOSTART: CommandChannel.Signal("ToggleAutostart"); return true;
            case ID_FREE: CommandChannel.Signal("ToggleFree"); return true;
            case ID_SORT_NAME: CommandChannel.Signal("SortName"); return true;
            case ID_SORT_DATE: CommandChannel.Signal("SortDate"); return true;
            case ID_SORT_SIZE: CommandChannel.Signal("SortSize"); return true;
            case ID_SORT_KIND: CommandChannel.Signal("SortKind"); return true;
            case ID_QUIT: CommandChannel.Signal("Quit"); return true;
            case ID_D_OPEN: CommandChannel.Signal("OpenSelection"); return true;
            case ID_D_CUT: CommandChannel.Signal("CutSelection"); return true;
            case ID_D_COPY: CommandChannel.Signal("CopySelection"); return true;
            case ID_D_RENAME: CommandChannel.Signal("RenameSelection"); return true;
            case ID_D_DELETE: CommandChannel.Signal("DeleteSelection"); return true;
            case ID_D_PROPS: CommandChannel.Signal("PropertiesSelection"); return true;
            case ID_D_OPENWITH:
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "rundll32.exe", $"shell32.dll,OpenAs_RunDLL {paths[0]}") { UseShellExecute = false });
                }
                catch (Exception ex) { Log.Write("OpenAs failed: " + ex.Message); }
                return true;
            default:
                return cmd >= 0x7000; // 未知本地 id：吞掉别当 shell 命令回传
        }
    }
}
