using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacDesk.Services;

/// <summary>
/// shell 菜单的跨进程序列化（前台战争的终极解）。
///
/// 背景：菜单 host 是独立进程，TrackPopupMenu 的菜单会被"关闭上一个菜单后系统分两站
/// 异步归还前台（主窗口 → Progman）"的落地扫掉——拖拽后连击时每个菜单要被杀两次，
/// settle-wait/重试只能缓解不能根治（机主仍见"大方框闪现即灭"）。
///
/// 终极解：host 只负责构建（QueryContextMenu 加载第三方扩展，崩了不伤主进程）——
/// 强制填充懒加载子菜单 → 把 HMENU 摘成纯数据树（文本/状态/图标位图/owner-draw 渲染
/// 捕获）→ 主进程在自己 UI 线程重建原生 HMENU 并 TrackPopupMenu。主窗口与 DefView
/// 共享 Explorer 输入队列（SetParent 跨进程副作用），菜单拿得到输入；激活风暴的两站
/// 都落在自己队列里，配合 owner 吞 WM_CANCELMODE，杀无可杀。
/// 选中的 shell 命令 id 回传 host 由同一个 IContextMenu 实例 InvokeCommand（隔离不变）。
/// </summary>
internal static class MenuSnapshot
{
    // ── 数据树 ────────────────────────────────────────────────

    internal sealed class Item
    {
        public bool Sep { get; set; }
        public uint Id { get; set; }
        public string Text { get; set; } = "";
        public bool Checked { get; set; }
        public bool Radio { get; set; }
        public bool Disabled { get; set; }
        public bool Default { get; set; }

        // hbmpItem 静态位图（BGRA 预乘、顶朝下）
        public int IconW { get; set; }
        public int IconH { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[]? Icon { get; set; }

        // MFT_OWNERDRAW 项：整项渲染捕获（不透明含菜单底色），normal/selected 两态
        public bool OwnerDraw { get; set; }
        public int OdW { get; set; }
        public int OdH { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[]? OdNormal { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[]? OdSelected { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<Item>? Children { get; set; }

        /// <summary>捕获时的菜单位置（仅 host 侧图标稳定化重读用；不序列化）。
        /// 用它而非 list 索引，因 GetMenuItemInfoW 失败时 Capture 会跳过该位不入 list，索引会错位。</summary>
        [JsonIgnore] public int Pos { get; set; }
    }

    public static string ToJson(List<Item> items) => JsonSerializer.Serialize(items);
    public static List<Item> FromJson(string json) => JsonSerializer.Deserialize<List<Item>>(json) ?? new();

    // ── Win32 ─────────────────────────────────────────────────

    private const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_DATA = 0x20,
                       MIIM_STRING = 0x40, MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
    private const uint MFT_OWNERDRAW = 0x100, MFT_RADIOCHECK = 0x200, MFT_SEPARATOR = 0x800;
    private const uint MFS_DISABLED = 0x3, MFS_CHECKED = 0x8, MFS_DEFAULT = 0x1000;
    private const uint ODS_SELECTED = 0x1;
    private const int WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_INITMENUPOPUP = 0x117;
    private const int COLOR_MENU = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFOW
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEASUREITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemWidth, itemHeight;
        public IntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DRAWITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemAction, itemState;
        public IntPtr hwndItem, hDC;
        public Interop.Native.RECT rcItem;
        public IntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMenuItemInfoW(IntPtr hMenu, uint item, bool byPos, ref MENUITEMINFOW mii);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenuItemW(IntPtr hMenu, uint item, bool byPos, ref MENUITEMINFOW mii);
    [DllImport("user32.dll")] internal static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern IntPtr GetSysColorBrush(int nIndex);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref Interop.Native.RECT lprc, IntPtr hbr);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr h, int c, out BITMAP pv);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref Interop.Native.BITMAPINFO bmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")]
    private static extern int SetDIBitsToDevice(IntPtr hdc, int xDest, int yDest, uint w, uint h,
        int xSrc, int ySrc, uint startScan, uint cLines, byte[] bits, ref Interop.Native.BITMAPINFO bmi, uint colorUse);

    private static Interop.Native.BITMAPINFO Bmi32(int w, int h) => new()
    {
        bmiHeader = new Interop.Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Interop.Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // 顶朝下
            biPlanes = 1,
            biBitCount = 32,
        },
        bmiColors = new uint[256],
    };

    // ── 捕获（host 侧） ───────────────────────────────────────

    /// <summary>
    /// 把 HMENU 摘成数据树。递归进子菜单前先经 IContextMenu2/3 强制 WM_INITMENUPOPUP
    /// （"新建/打开方式/发送到"都是懒填充，不喂这口就是空的）。
    /// </summary>
    public static List<Item> Capture(IntPtr hMenu, object? menuObj, int depth = 0)
    {
        var list = new List<Item>();
        int n = GetMenuItemCount(hMenu);
        for (int i = 0; i < n; i++)
        {
            var mii = new MENUITEMINFOW
            {
                cbSize = Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_FTYPE | MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_BITMAP | MIIM_DATA,
            };
            if (!GetMenuItemInfoW(hMenu, (uint)i, true, ref mii)) continue;

            var it = new Item
            {
                Id = mii.wID,
                Pos = i,
                Checked = (mii.fState & MFS_CHECKED) != 0,
                Radio = (mii.fType & MFT_RADIOCHECK) != 0,
                Disabled = (mii.fState & MFS_DISABLED) != 0,
                Default = (mii.fState & MFS_DEFAULT) != 0,
            };

            if ((mii.fType & MFT_SEPARATOR) != 0)
            {
                it.Sep = true;
                list.Add(it);
                continue;
            }

            it.Text = ItemText(hMenu, (uint)i);

            if ((mii.fType & MFT_OWNERDRAW) != 0 && menuObj != null)
            {
                it.OwnerDraw = true;
                RenderOwnerDraw(menuObj, hMenu, mii.wID, mii.dwItemData, it);
            }
            else if (mii.hbmpItem != IntPtr.Zero && (long)mii.hbmpItem > 11) // 排除 HBMMENU_* 常量（-1、1..11）
            {
                CopyBitmap(mii.hbmpItem, it);
            }

            if (mii.hSubMenu != IntPtr.Zero && depth < 4)
            {
                ForceInit(menuObj, mii.hSubMenu, i);
                it.Children = Capture(mii.hSubMenu, menuObj, depth + 1);
            }

            list.Add(it);
        }
        RestoreIconsFromCache(list);  // 先回填历史见过的图标（缩小稳定化等待面 + 治随机丢的主力）
        StabilizeIcons(hMenu, list);  // 仍缺的泵消息等异步加载（顺带把新图标喂进缓存）
        StoreIconsToCache(list);      // 这轮捕到的（含刚等到的）存缓存，供下次回填
        return list;
    }

    /// <summary>图标粘滞缓存（host 进程常驻）：shell 对 IExplorerCommand 类动词（"在终端打开"等）
    /// 的图标是异步/按需的，而 v2 从不真正显示菜单 → 某次没 landed 就整项丢图，且每次丢的不一样。
    /// 凡**某次捕获到过**的图标按显示文本记住，后续缺图即回填——"见过一次就不再丢"，把随机丢图
    /// 收敛掉。host 单线程串行建菜单，无需加锁；键=文本（同文本同图标，冲突可忽略）。</summary>
    private static readonly Dictionary<string, (byte[] icon, int w, int h)> _iconCache = new();

    private static void RestoreIconsFromCache(List<Item> list)
    {
        foreach (var it in list)
            if (!it.Sep && !it.OwnerDraw && it.Icon == null && !string.IsNullOrEmpty(it.Text)
                && _iconCache.TryGetValue(it.Text, out var c))
            { it.Icon = c.icon; it.IconW = c.w; it.IconH = c.h; }
    }

    private static void StoreIconsToCache(List<Item> list)
    {
        foreach (var it in list)
            if (!it.Sep && !it.OwnerDraw && it.Icon != null && it.IconW > 0 && !string.IsNullOrEmpty(it.Text))
                _iconCache[it.Text] = (it.Icon, it.IconW, it.IconH);
    }

    /// <summary>菜单图标随机丢失的修复。shell 对不少动词的图标是**异步加载**的
    /// （IExplorerCommand 类如"在终端打开"、ShellNew 的"新建▸"子项等）：单次即时快照会漏掉
    /// 当刻还没 landed 的图标，而 v2 显示的是重建菜单、shell 后到的图标永远追不上 → 每次随机
    /// 丢不同的图。这里泵消息（host STA 期间不泵，异步完成消息到不了、hbmpItem 停在 0）给图标
    /// 留时间、按位置只重读仍缺图的项，直到补齐 / 连续空转（剩下的是本就无图的项）/ 硬上限。
    /// **暖菜单（全有图）零等待。** list 索引 1:1 对应菜单位置（含分隔符，逐项按序 Add）。</summary>
    private static void StabilizeIcons(IntPtr hMenu, List<Item> list)
    {
        bool AnyMissing()
        {
            foreach (var it in list)
                if (!it.Sep && !it.OwnerDraw && it.Icon == null) return true;
            return false;
        }
        if (!AnyMissing()) return; // 暖菜单：零开销直接返回

        int deadline = Environment.TickCount + 200; // 硬上限（菜单本就有探针延迟，+≤200ms 无感）
        int idle = 0;
        while (Environment.TickCount < deadline && idle < 2) // 连续 2 次无新图=剩下的本就无图，收手
        {
            PumpFor(30);
            bool gained = false;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it.Sep || it.OwnerDraw || it.Icon != null) continue;
                var mii = new MENUITEMINFOW { cbSize = Marshal.SizeOf<MENUITEMINFOW>(), fMask = MIIM_BITMAP };
                if (GetMenuItemInfoW(hMenu, (uint)it.Pos, true, ref mii) && (long)mii.hbmpItem > 11)
                {
                    CopyBitmap(mii.hbmpItem, it); // 失败仍留 Icon==null，下轮再试（对瞬时 GetDIBits 失败天然容错）
                    if (it.Icon != null) gained = true;
                }
            }
            idle = gained ? 0 : idle + 1;
            if (!AnyMissing()) break; // 全补齐即走
        }
    }

    /// <summary>泵消息 + 睡满 ms：host STA 线程在 ForceInit→Capture 窗口不泵的话，shell 以
    /// 投递消息形式送达的图标完成到不了 owner 窗口，hbmpItem 永远读回 0。</summary>
    private static void PumpFor(int ms)
    {
        int t0 = Environment.TickCount;
        do
        {
            while (Interop.Native.PeekMessageW(out var m, IntPtr.Zero, 0, 0, Interop.Native.PM_REMOVE))
            {
                Interop.Native.TranslateMessage(ref m);
                Interop.Native.DispatchMessageW(ref m);
            }
            System.Threading.Thread.Sleep(10);
        } while (Environment.TickCount - t0 < ms);
    }

    private static string ItemText(IntPtr hMenu, uint pos)
    {
        var mii = new MENUITEMINFOW { cbSize = Marshal.SizeOf<MENUITEMINFOW>(), fMask = MIIM_STRING };
        if (!GetMenuItemInfoW(hMenu, pos, true, ref mii) || mii.cch == 0) return "";
        var buf = Marshal.AllocHGlobal((int)(mii.cch + 1) * 2);
        try
        {
            mii.dwTypeData = buf;
            mii.cch += 1;
            mii.fMask = MIIM_STRING;
            return GetMenuItemInfoW(hMenu, pos, true, ref mii) ? Marshal.PtrToStringUni(buf) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>给懒填充（子）菜单喂 WM_INITMENUPOPUP（经 IContextMenu3.HandleMenuMsg2 / 2.HandleMenuMsg）。</summary>
    public static void ForceInit(object? menuObj, IntPtr hSubMenu, int pos)
    {
        if (menuObj == null) return;
        try
        {
            var lParam = (IntPtr)(pos & 0xFFFF);
            if (menuObj is Interop.ShellCom.IContextMenu3 cm3)
                cm3.HandleMenuMsg2((uint)WM_INITMENUPOPUP, hSubMenu, lParam, IntPtr.Zero);
            else if (menuObj is Interop.ShellCom.IContextMenu2 cm2)
                cm2.HandleMenuMsg((uint)WM_INITMENUPOPUP, hSubMenu, lParam);
        }
        catch (Exception ex) { Log.Write($"submenu init failed at pos {pos}: {ex.Message}"); }
    }

    /// <summary>hbmpItem 静态位图 → 32bpp BGRA 字节（保留 alpha，shell 的 PARGB 位图无损往返）。</summary>
    private static void CopyBitmap(IntPtr hbmp, Item it)
    {
        try
        {
            if (GetObjectW(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return;
            var bmi = Bmi32(bm.bmWidth, bm.bmHeight);
            var bits = new byte[bm.bmWidth * bm.bmHeight * 4];
            IntPtr dc = Interop.Native.GetDC(IntPtr.Zero);
            try
            {
                if (Interop.Native.GetDIBits(dc, hbmp, 0, (uint)bm.bmHeight, bits, ref bmi, 0) == 0) return;
            }
            finally { Interop.Native.ReleaseDC(IntPtr.Zero, dc); }
            // 24bpp 来源没有 alpha 通道，补成不透明
            if (bm.bmBitsPixel < 32)
                for (int p = 3; p < bits.Length; p += 4) bits[p] = 0xFF;
            it.IconW = bm.bmWidth;
            it.IconH = bm.bmHeight;
            it.Icon = bits;
        }
        catch (Exception ex) { Log.Write("menu bitmap copy failed: " + ex.Message); }
    }

    /// <summary>
    /// owner-draw 项离屏渲染捕获：合成 WM_MEASUREITEM 拿尺寸，再喂 WM_DRAWITEM 让
    /// shell 扩展画进我们的内存 DC（normal / selected 两态，底色 COLOR_MENU 不透明）。
    /// </summary>
    private static void RenderOwnerDraw(object menuObj, IntPtr hMenu, uint id, IntPtr itemData, Item it)
    {
        try
        {
            var mis = new MEASUREITEMSTRUCT { CtlType = 1 /* ODT_MENU */, itemID = id, itemData = itemData };
            var pMis = Marshal.AllocHGlobal(Marshal.SizeOf<MEASUREITEMSTRUCT>());
            try
            {
                Marshal.StructureToPtr(mis, pMis, false);
                Forward(menuObj, WM_MEASUREITEM, IntPtr.Zero, pMis);
                mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(pMis);
            }
            finally { Marshal.FreeHGlobal(pMis); }
            if (mis.itemWidth == 0 || mis.itemHeight == 0 || mis.itemWidth > 2000 || mis.itemHeight > 200) return;

            it.OdW = (int)mis.itemWidth;
            it.OdH = (int)mis.itemHeight;
            it.OdNormal = RenderState(menuObj, hMenu, id, itemData, it.OdW, it.OdH, 0);
            it.OdSelected = RenderState(menuObj, hMenu, id, itemData, it.OdW, it.OdH, ODS_SELECTED);
        }
        catch (Exception ex) { Log.Write($"ownerdraw capture failed id={id}: {ex.Message}"); }
    }

    private static byte[]? RenderState(object menuObj, IntPtr hMenu, uint id, IntPtr itemData, int w, int h, uint state)
    {
        IntPtr memDC = CreateCompatibleDC(IntPtr.Zero);
        if (memDC == IntPtr.Zero) return null;
        var bmi = Bmi32(w, h);
        IntPtr dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr pv, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(memDC); return null; }
        try
        {
            IntPtr old = SelectObject(memDC, dib);
            var rc = new Interop.Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            FillRect(memDC, ref rc, GetSysColorBrush(COLOR_MENU));

            var dis = new DRAWITEMSTRUCT
            {
                CtlType = 1, itemID = id, itemAction = 1 /* ODA_DRAWENTIRE */, itemState = state,
                hwndItem = hMenu, hDC = memDC, rcItem = rc, itemData = itemData,
            };
            var pDis = Marshal.AllocHGlobal(Marshal.SizeOf<DRAWITEMSTRUCT>());
            try
            {
                Marshal.StructureToPtr(dis, pDis, false);
                Forward(menuObj, WM_DRAWITEM, IntPtr.Zero, pDis);
            }
            finally { Marshal.FreeHGlobal(pDis); }

            var bits = new byte[w * h * 4];
            Marshal.Copy(pv, bits, 0, bits.Length);
            for (int p = 3; p < bits.Length; p += 4) bits[p] = 0xFF; // GDI 文字绘制会踩 alpha，整体置不透明
            SelectObject(memDC, old);
            return bits;
        }
        finally
        {
            DeleteObjectSafe(dib);
            DeleteDC(memDC);
        }
    }

    private static void DeleteObjectSafe(IntPtr h) { try { Interop.Native.DeleteObject(h); } catch { } }

    private static void Forward(object menuObj, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (menuObj is Interop.ShellCom.IContextMenu3 cm3) cm3.HandleMenuMsg2((uint)msg, wParam, lParam, IntPtr.Zero);
        else if (menuObj is Interop.ShellCom.IContextMenu2 cm2) cm2.HandleMenuMsg((uint)msg, wParam, lParam);
    }

    // ── 重建（主进程侧） ──────────────────────────────────────

    /// <summary>重建出的原生菜单 + 需随菜单存活的 GDI 资源 + owner-draw 项的像素表。</summary>
    internal sealed class Built : IDisposable
    {
        public IntPtr Handle;
        public readonly List<IntPtr> Bitmaps = new();
        public readonly Dictionary<uint, Item> OwnerDrawn = new();

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { DestroyMenu(Handle); Handle = IntPtr.Zero; }
            foreach (var b in Bitmaps) DeleteObjectSafe(b);
            Bitmaps.Clear();
        }
    }

    public static Built Build(List<Item> items)
    {
        var built = new Built { Handle = CreatePopupMenu() };
        Fill(built.Handle, items, built);
        return built;
    }

    private static void Fill(IntPtr hMenu, List<Item> items, Built built)
    {
        uint pos = 0;
        foreach (var it in items)
        {
            var mii = new MENUITEMINFOW
            {
                cbSize = Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_FTYPE | MIIM_ID | MIIM_STATE,
                wID = it.Id,
            };
            if (it.Sep)
            {
                mii.fType = MFT_SEPARATOR;
                InsertMenuItemW(hMenu, pos++, true, ref mii);
                continue;
            }

            mii.fState = (it.Checked ? MFS_CHECKED : 0) | (it.Disabled ? MFS_DISABLED : 0) | (it.Default ? MFS_DEFAULT : 0);
            if (it.Radio) mii.fType |= MFT_RADIOCHECK;

            GCHandle text = default;
            try
            {
                if (it.OwnerDraw && it.OdNormal != null)
                {
                    mii.fType |= MFT_OWNERDRAW;
                    mii.dwItemData = (IntPtr)it.Id;
                    built.OwnerDrawn[it.Id] = it;
                }
                else
                {
                    mii.fMask |= MIIM_STRING;
                    text = GCHandle.Alloc(it.Text + "\0", GCHandleType.Pinned);
                    mii.dwTypeData = text.AddrOfPinnedObject();
                    if (it.Icon != null && it.IconW > 0)
                    {
                        var hbmp = MakeDib(it.IconW, it.IconH, it.Icon);
                        if (hbmp != IntPtr.Zero)
                        {
                            mii.fMask |= MIIM_BITMAP;
                            mii.hbmpItem = hbmp;
                            built.Bitmaps.Add(hbmp);
                        }
                    }
                }

                if (it.Children != null)
                {
                    var sub = CreatePopupMenu();
                    Fill(sub, it.Children, built);
                    mii.fMask |= MIIM_SUBMENU;
                    mii.hSubMenu = sub; // 挂进父菜单后随父一起销毁
                }

                InsertMenuItemW(hMenu, pos++, true, ref mii);
            }
            finally { if (text.IsAllocated) text.Free(); }
        }
    }

    private static IntPtr MakeDib(int w, int h, byte[] bgra)
    {
        var bmi = Bmi32(w, h);
        IntPtr dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr pv, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || pv == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(bgra, 0, pv, Math.Min(bgra.Length, w * h * 4));
        return dib;
    }

    // ── 主进程 owner-draw 回放（owner 窗口的 WndProc 里调用） ──

    /// <summary>WM_MEASUREITEM：回放捕获尺寸。认领返回 true。</summary>
    public static bool OnMeasureItem(Built? built, IntPtr lParam)
    {
        if (built == null) return false;
        var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(lParam);
        if (mis.CtlType != 1 || !built.OwnerDrawn.TryGetValue((uint)mis.itemData, out var it)) return false;
        mis.itemWidth = (uint)it.OdW;
        mis.itemHeight = (uint)it.OdH;
        Marshal.StructureToPtr(mis, lParam, false);
        return true;
    }

    /// <summary>WM_DRAWITEM：把捕获位图按状态贴回。认领返回 true。</summary>
    public static bool OnDrawItem(Built? built, IntPtr lParam)
    {
        if (built == null) return false;
        var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
        if (dis.CtlType != 1 || !built.OwnerDrawn.TryGetValue((uint)dis.itemData, out var it)) return false;
        var bits = (dis.itemState & ODS_SELECTED) != 0 ? it.OdSelected ?? it.OdNormal : it.OdNormal;
        if (bits == null) return false;

        // 系统给的 rcItem 可能比捕获宽（菜单整体宽度取最宽项）：先铺底色再贴位图
        FillRect(dis.hDC, ref dis.rcItem, GetSysColorBrush(COLOR_MENU));
        var bmi = Bmi32(it.OdW, it.OdH);
        SetDIBitsToDevice(dis.hDC, dis.rcItem.Left, dis.rcItem.Top, (uint)it.OdW, (uint)it.OdH,
            0, 0, 0, (uint)it.OdH, bits, ref bmi, 0);
        return true;
    }

    // ── 侦察模式（--menudump）：真机上看清菜单项的真实结构 ────

    public static void DumpLog(List<Item> items, string indent = "")
    {
        foreach (var it in items)
        {
            if (it.Sep) { Log.Write($"{indent}────"); continue; }
            string icon = it.Icon != null ? $" bmp={it.IconW}x{it.IconH}" :
                          it.OwnerDraw ? $" OD={it.OdW}x{it.OdH}{(it.OdNormal != null ? "√" : "×")}" : "";
            string flags = (it.Checked ? " chk" : "") + (it.Disabled ? " dis" : "") + (it.Default ? " def" : "");
            Log.Write($"{indent}[{it.Id}] \"{it.Text}\"{icon}{flags}");
            if (it.Children != null) DumpLog(it.Children, indent + "  ");
        }
    }
}
