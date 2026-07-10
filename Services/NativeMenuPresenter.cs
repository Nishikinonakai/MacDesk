using System.IO;
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

    // ── 菜单项文本目录（设置 GUI 的黑名单预设选择用） ─────────
    // 每次弹菜单顺手收集见过的项目文本（含子菜单），设置窗口里从目录直接选着屏蔽，
    // 不用手打子串。会话级缓存即可（右键几次就齐了）。

    private static readonly object _catalogLock = new();
    private static readonly HashSet<string> _menuItemCatalog = new(StringComparer.OrdinalIgnoreCase);

    public static string[] MenuItemCatalog
    {
        get { lock (_catalogLock) return _menuItemCatalog.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
    }

    /// <summary>只喂 shell 部分（追加自定义项之前调用——自家功能项/子菜单头不进目录）。
    /// 文本剥掉加速键 &，与 StripBlacklisted 的比对口径一致。</summary>
    public static void Catalog(List<MenuSnapshot.Item> items)
    {
        lock (_catalogLock)
        {
            if (_menuItemCatalog.Count > 400) return; // 防失控，够用就行
            CatalogCore(items);
        }
    }

    private static void CatalogCore(List<MenuSnapshot.Item> items)
    {
        foreach (var it in items)
        {
            if (!it.Sep && it.Text.Length > 0) _menuItemCatalog.Add(it.Text.Replace("&", ""));
            if (it.Children != null) CatalogCore(it.Children);
        }
    }

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
        finally
        {
            Current = null;
            KillTimer(ownerHwnd, RedrawTimerId); // 补绘定时器随菜单收工
            _preExisting.Clear();
            _erased.Clear();
        }
    }

    // ── 子菜单白块修复 ────────────────────────────────────────
    // TPM_NOANIMATION 只管顶层弹出；子菜单自带淡入动画，而非前台线程的菜单动画不渲染，
    // 表现为白块、划过哪行显影哪行（真机复现，与深夜批次的顶层白块同病）。
    // 修法：子菜单的 WM_INITMENUPOPUP 后短定时器补绘。**只碰本次弹出后新出现的菜单窗口、
    // 每个只补一次、顶层菜单完全不碰**——第一版无差别连刷 6 拍（带 ERASE），机主实测
    // 已画好的菜单项肉眼可见地闪烁几次；白块只可能出现在新弹出的窗口上，补它就够。

    private static readonly UIntPtr RedrawTimerId = (UIntPtr)0x4D52; // 'MR'
    private static int _redrawTicks;
    private static readonly HashSet<long> _preExisting = new(); // 弹出前已可见的菜单窗口（已画好，不碰）
    private static readonly HashSet<long> _erased = new();      // 已带 ERASE 补绘过首拍的新窗口

    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80,
                       RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;

    /// <summary>owner 的 WM_INITMENUPOPUP。wParam = 正在弹出的 HMENU。</summary>
    public static void OnInitMenuPopup(IntPtr ownerHwnd, IntPtr hMenuPopup)
    {
        if (!MenuOpen) return;
        // 顶层菜单 TPM_NOANIMATION 即时绘制，无需补绘（补绘 = 白闪）
        if (Current != null && hMenuPopup == Current.Handle) return;

        // 快照当前已可见的菜单窗口：它们已画好。直接换子菜单（A 关 B 开）时 A 先隐藏
        // 再来 B 的 INITMENUPOPUP，所以复用的窗口不会被误判为"已画好"。
        _preExisting.Clear();
        _erased.Clear();
        IntPtr w = IntPtr.Zero;
        while ((w = FindWindowExW(IntPtr.Zero, w, "#32768", null)) != IntPtr.Zero)
            if (IsWindowVisible(w)) _preExisting.Add((long)w);

        _redrawTicks = 0;
        SetTimer(ownerHwnd, RedrawTimerId, 60, IntPtr.Zero);
    }

    /// <summary>owner 的 WM_TIMER。认领返回 true。
    /// 新窗口每拍都补（单拍不够：淡入动画期窗口是分层窗口，太早的补绘不上屏，
    /// 真机踩坑），但只有首拍带 ERASE——空白窗口擦除无闪烁代价，后续拍对已画好内容
    /// 重绘同像素肉眼不可见，主菜单（preExisting）全程不碰。</summary>
    public static bool OnTimer(IntPtr ownerHwnd, IntPtr timerId)
    {
        if ((ulong)timerId != (ulong)RedrawTimerId) return false;
        IntPtr w = IntPtr.Zero;
        while ((w = FindWindowExW(IntPtr.Zero, w, "#32768", null)) != IntPtr.Zero)
        {
            if (!IsWindowVisible(w) || _preExisting.Contains((long)w)) continue;
            uint flags = RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN;
            if (_erased.Add((long)w)) flags |= RDW_ERASE | RDW_FRAME; // 首拍才擦
            RedrawWindow(w, IntPtr.Zero, IntPtr.Zero, flags);
        }
        if (++_redrawTicks >= 5 || !MenuOpen) KillTimer(ownerHwnd, RedrawTimerId);
        return true;
    }

    // ── MacDesk 自定义项 / 降级菜单（主进程本地 id，与 host 的 shell id 空间无关） ──

    private const uint ID_ARRANGE = 0x7001, ID_UNDO = 0x7002, ID_TOGGLE = 0x7003, ID_QUIT = 0x7004,
                       ID_AUTOSTART = 0x7005, ID_SORT_NAME = 0x7006, ID_SORT_DATE = 0x7007,
                       ID_SORT_SIZE = 0x7008, ID_SORT_KIND = 0x7009, ID_FREE = 0x700A,
                       ID_SETTINGS = 0x700B, ID_PERSONALIZE = 0x700C,
                       ID_STACKS = 0x700D, ID_GROUP_KIND = 0x700E,
                       ID_GROUP_DATE = 0x700F, ID_GROUP_SIZE = 0x7010;
    private const uint ID_D_OPEN = 0x7101, ID_D_OPENWITH = 0x7102, ID_D_CUT = 0x7103, ID_D_COPY = 0x7104,
                       ID_D_RENAME = 0x7105, ID_D_DELETE = 0x7106, ID_D_PROPS = 0x7107;

    private static MenuSnapshot.Item Sep() => new() { Sep = true };
    private static MenuSnapshot.Item Cmd(uint id, string text, bool check = false) =>
        new() { Id = id, Text = text, Checked = check };

    /// <summary>背景菜单尾部的 MacDesk 自定义项（macOS 克制风格，机主定案）：
    /// 未叠放 = 使用叠放 / 排序方式▸（"无（自由摆放）"= macOS Sort By > None）/ 整理 / 撤销；
    /// 已叠放 = ✓使用叠放 / 分组依据▸（与 macOS 一致，菜单语义随开关切换）。
    /// 自启、退出、原生图标开关住在设置窗口。</summary>
    public static List<MenuSnapshot.Item> CustomBackgroundItems()
    {
        var cfg = MacDesk.Desktop.Config;
        if (cfg.UseStacks)
            return new()
            {
                Sep(),
                Cmd(ID_STACKS, L.T("使用叠放", "Use Stacks"), true),
                new MenuSnapshot.Item
                {
                    Text = L.T("分组依据", "Group Stacks By"),
                    Children = new List<MenuSnapshot.Item>
                    {
                        Cmd(ID_GROUP_KIND, L.T("类型", "Kind"), cfg.StackGroupBy == "kind"),
                        Cmd(ID_GROUP_DATE, L.T("修改日期", "Date Modified"), cfg.StackGroupBy == "date"),
                        Cmd(ID_GROUP_SIZE, L.T("大小", "Size"), cfg.StackGroupBy == "size"),
                    },
                },
                Sep(),
                Cmd(ID_PERSONALIZE, L.T("更换壁纸…", "Change Wallpaper…")),
                Cmd(ID_SETTINGS, L.T("MacDesk 设置…", "MacDesk Settings…")),
            };
        return new()
        {
            Sep(),
            Cmd(ID_STACKS, L.T("使用叠放", "Use Stacks")),
            new MenuSnapshot.Item
            {
                Text = L.T("排序方式", "Sort By"),
                Children = new List<MenuSnapshot.Item>
                {
                    Cmd(ID_FREE, L.T("无（自由摆放）", "None (Free Placement)"), cfg.FreePlacement),
                    Sep(),
                    Cmd(ID_SORT_NAME, L.T("名称", "Name")), Cmd(ID_SORT_DATE, L.T("修改日期", "Date Modified")),
                    Cmd(ID_SORT_SIZE, L.T("大小", "Size")), Cmd(ID_SORT_KIND, L.T("类型", "Kind")),
                },
            },
            Cmd(ID_ARRANGE, L.T("按 mac 式网格整理", "Clean Up (mac-style Grid)")),
            Cmd(ID_UNDO, L.T("撤销上次整理", "Undo Clean Up")),
            Sep(),
            Cmd(ID_PERSONALIZE, L.T("更换壁纸…", "Change Wallpaper…")),
            Cmd(ID_SETTINGS, L.T("MacDesk 设置…", "MacDesk Settings…")),
        };
    }

    /// <summary>降级文件菜单（探针判定该类型原生菜单必崩时）。动词与旧 host 版一致。</summary>
    public static List<MenuSnapshot.Item> DegradedFileItems(string[] paths)
    {
        var items = new List<MenuSnapshot.Item> { Cmd(ID_D_OPEN, L.T("打开", "Open")) };
        if (paths.Length == 1 && !paths[0].StartsWith("::")) items.Add(Cmd(ID_D_OPENWITH, L.T("打开方式…", "Open With…")));
        items.Add(Sep());
        items.Add(Cmd(ID_D_CUT, L.T("剪切", "Cut")));
        items.Add(Cmd(ID_D_COPY, L.T("复制", "Copy")));
        items.Add(Sep());
        if (paths.Length == 1) items.Add(Cmd(ID_D_RENAME, L.T("重命名", "Rename")));
        items.Add(Cmd(ID_D_DELETE, L.T("删除", "Delete")));
        items.Add(Sep());
        items.Add(Cmd(ID_D_PROPS, L.T("属性", "Properties")));
        return items;
    }

    // ── Locale Emulator 兼容项 ────────────────────────────────
    // LEContextMenuHandler 是 mscoree 托管扩展，在我们进程里必炸（被 ManagedHandlerShield
    // 长期屏蔽），LE 自己的菜单项永远无法出现在序列化菜单里。补偿：检测到 LE 已装且
    // 右键目标是 .exe（或解析到 .exe 的快捷方式）时，自绘"用 Locale Emulator 运行"
    // 直接调 LEProc.exe -run。

    private const uint ID_LE_RUN = 0x7201, ID_NEWFOLDER_SEL = 0x7202, ID_FOLDER_STACK = 0x7203;
    private static string? _leTarget; // Append 时解析（UI/STA 线程），Dispatch 时消费

    /// <summary>多选文件菜单追加"用所选项目新建文件夹"（Finder 行为）。</summary>
    public static void AppendSelectionItems(List<MenuSnapshot.Item> items, string[] paths)
    {
        var real = paths.Count(p => !p.StartsWith("::"));
        if (real < 2) return;
        items.Add(Sep());
        items.Add(Cmd(ID_NEWFOLDER_SEL, L.T($"用所选项目新建文件夹（{real} 项）", $"New Folder with Selection ({real} Items)")));
    }

    /// <summary>叠放模式下单选桌面根文件夹 → 追加"以堆叠方式展示"勾选项
    /// （文件夹堆叠，issue #2：单击原地展开内容，Dock 文件夹堆叠语义）。</summary>
    public static void AppendFolderStackItem(List<MenuSnapshot.Item> items, string[] paths)
    {
        var cfg = MacDesk.Desktop.Config;
        if (!cfg.UseStacks || paths.Length != 1 || paths[0].StartsWith("::")) return;
        string p = paths[0];
        try { if (!Directory.Exists(p)) return; } catch { return; }
        var parent = Path.GetDirectoryName(p);
        if (!string.Equals(parent, DesktopItemProvider.UserDesktop, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parent, DesktopItemProvider.PublicDesktop, StringComparison.OrdinalIgnoreCase))
            return; // 只认桌面根上的文件夹（展开视图里的子文件夹不再嵌套堆叠）
        bool on = cfg.StackFolders.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase));
        items.Add(Sep());
        items.Add(Cmd(ID_FOLDER_STACK, L.T("以堆叠方式展示", "Display as Stack"), on));
    }

    /// <summary>单选文件菜单补"重命名"：QueryContextMenu 不传 CMF_CANRENAME 时 shell
    /// 直接省略该动词；而就算传了，重命名也是资源管理器**视图**实现的行为、不是可
    /// InvokeCommand 的菜单动词——无视图宿主里点了也不会有任何反应。所以自己补一项，
    /// 复用 ID_D_RENAME 分发（Signal RenameSelection → MacDesk 内联重命名框）。
    /// 位置紧跟 shell 的"删除"（剥 &amp; 后按菜单文本找，保住 Explorer 肌肉记忆位）；
    /// 文本对不上（第三方魔改/系统语言与应用语言不一致）退化为"属性"之前，再不行尾插。
    /// 思路来自 @climashscape 的 PR #5，锚点从本地 id 改为文本匹配——原生菜单里只有
    /// 1..0x6FFF 的 shell id，拿 ID_D_DELETE 找永远落空。</summary>
    public static void AppendRenameItem(List<MenuSnapshot.Item> items, string[] paths)
    {
        if (paths.Length != 1 || paths[0].StartsWith("::")) return;
        if (items.Any(it => it.Id == ID_D_RENAME)) return; // 降级菜单自带，别重复
        var rename = Cmd(ID_D_RENAME, L.T("重命名", "Rename"));
        static bool TextIs(MenuSnapshot.Item it, string zh, string en)
        {
            if (it.Sep || it.Children != null) return false;
            var t = it.Text.Replace("&", "").Trim();
            return t.Equals(zh, StringComparison.Ordinal) || t.Equals(en, StringComparison.OrdinalIgnoreCase);
        }
        int del = items.FindIndex(it => TextIs(it, "删除", "Delete"));
        if (del >= 0) { items.Insert(del + 1, rename); return; }
        int props = items.FindIndex(it => TextIs(it, "属性", "Properties"));
        if (props >= 0) { items.Insert(props, rename); return; }
        items.Add(rename);
    }

    private static readonly Lazy<string?> LeProcPath = new(() =>
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                @"CLSID\{C52B9871-E5E9-41FD-B84D-C5ACADBEC7AE}\InprocServer32");
            if (key == null) return null;
            // 托管 COM 注册：默认值 = mscoree.dll，真实程序集路径在 CodeBase（本键或版本子键，file:/// URI）
            var codebase = key.GetValue("CodeBase") as string;
            if (codebase == null)
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var vk = key.OpenSubKey(sub);
                    if ((codebase = vk?.GetValue("CodeBase") as string) != null) break;
                }
            if (codebase == null) return null;
            var proc = Path.Combine(Path.GetDirectoryName(new Uri(codebase).LocalPath)!, "LEProc.exe");
            return File.Exists(proc) ? proc : null;
        }
        catch { return null; }
    });

    /// <summary>菜单弹出前追加 LE 项（须在 STA/UI 线程：IShellLink 解析快捷方式）。</summary>
    public static void AppendLeRunItem(List<MenuSnapshot.Item> items, string[] paths)
    {
        _leTarget = null;
        try
        {
            if (LeProcPath.Value == null || paths.Length != 1 || paths[0].StartsWith("::")) return;
            string p = paths[0], target;
            switch (Path.GetExtension(p).ToLowerInvariant())
            {
                case ".exe": target = p; break;
                case ".lnk":
                    var link = new Interop.ShellCom.ShellLink();
                    ((Interop.ShellCom.IPersistFile)link).Load(p, 0);
                    var sb = new System.Text.StringBuilder(1024);
                    if (((Interop.ShellCom.IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0) return;
                    target = sb.ToString();
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return;
                    break;
                default: return;
            }
            _leTarget = target;
            items.Add(Sep());
            items.Add(Cmd(ID_LE_RUN, L.T("用 Locale Emulator 运行", "Run with Locale Emulator")));
        }
        catch (Exception ex) { Log.Write("LE item skipped: " + ex.Message); }
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
            case ID_SETTINGS: CommandChannel.Signal("OpenSettings"); return true;
            case ID_STACKS: CommandChannel.Signal("ToggleStacks"); return true;
            case ID_GROUP_KIND: CommandChannel.Signal("GroupKind"); return true;
            case ID_GROUP_DATE: CommandChannel.Signal("GroupDate"); return true;
            case ID_GROUP_SIZE: CommandChannel.Signal("GroupSize"); return true;
            case ID_PERSONALIZE:
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "ms-settings:personalization-background") { UseShellExecute = true });
                }
                catch (Exception ex) { Log.Write("open personalization failed: " + ex.Message); }
                return true;
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
            case ID_NEWFOLDER_SEL: CommandChannel.Signal("NewFolderWithSelection"); return true;
            case ID_FOLDER_STACK when paths.Length > 0:
                // 直达通道：被点的路径就在手里，不走 Signal→选中集反推（多屏下跨窗残留
                // 选中会让 SelectionWindow 选错窗——旧路径无载荷只能那么干，v2 不必）
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    MacDesk.MainWindow.ToggleFolderStackFlag(paths[0]));
                return true;
            case ID_LE_RUN when _leTarget != null && LeProcPath.Value != null:
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(LeProcPath.Value) { UseShellExecute = false };
                    psi.ArgumentList.Add("-run");
                    psi.ArgumentList.Add(_leTarget);
                    psi.WorkingDirectory = Path.GetDirectoryName(_leTarget) ?? "";
                    System.Diagnostics.Process.Start(psi);
                    Log.Write($"LE run: {_leTarget}");
                }
                catch (Exception ex) { Log.Write("LE run failed: " + ex.Message); }
                return true;
            default:
                return cmd >= 0x7000; // 未知本地 id：吞掉别当 shell 命令回传
        }
    }
}
