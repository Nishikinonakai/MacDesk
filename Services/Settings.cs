using System.IO;
using System.Text.Json;

namespace MacDesk.Services;

/// <summary>轻量用户设置（%LOCALAPPDATA%\MacDesk\settings.json）。目前只有自由摆放开关。</summary>
internal sealed class Settings
{
    private readonly string _file;

    /// <summary>自由摆放：拖放落点即位置、重排不吸附网格（macOS arrangeBy=none）。默认关（Windows 习惯网格）。</summary>
    public bool FreePlacement { get; set; }

    /// <summary>右键菜单黑名单：菜单项文本含任一子串（不分大小写）即被移除。设置 GUI 可增删。
    /// 默认屏蔽：AMD 项（机主点名）+ "授予访问权限"（桌面场景纯噪音的网络共享向导，中英双杀）。</summary>
    public List<string> MenuBlacklist { get; set; } = new() { "AMD Software", "Give access to", "授予访问权限" };

    /// <summary>使用叠放（macOS Use Stacks）：文件按类型聚成堆、自动右上列流，文件夹保持独立。
    /// 开启期间不写规范布局，关闭即恢复原摆放。</summary>
    public bool UseStacks { get; set; }

    /// <summary>叠放的分组依据："kind"（类型，默认）/"date"（修改日期）/"size"（大小）。
    /// 只在 UseStacks 时生效。</summary>
    public string StackGroupBy { get; set; } = "kind";

    /// <summary>以堆叠方式展示的桌面文件夹（完整路径，比较一律忽略大小写）。叠放模式下
    /// 这些文件夹带向下角标、单击原地展开内容（macOS Dock 文件夹堆叠语义，issue #2）。
    /// 文件夹被删/外部改名后由 Desktop.RefreshAll 剪枝；自家重命名在 CommitRename 过户。</summary>
    public List<string> StackFolders { get; set; } = new();

    /// <summary>菜单序列化进主进程同线程弹出（前台战争终极解）。false = 回退旧 host 内
    /// TrackPopupMenu 路径（settle-wait+重试），新路径出问题时的免重建逃生口。</summary>
    public bool MenuInMainProcess { get; set; } = true;

    /// <summary>强调色 key（见 Accent.Palette），影响选中标签/框选颜色。</summary>
    public string AccentColor { get; set; } = "blue";

    /// <summary>动态壁纸兼容（Wallpaper Engine）：检测到 WE 的每屏渲染窗即收编进桌面层
    /// （三明治 v3：图标呈现层 → WE 窗 → WPF 输入层），WE 原生渲染零开销。默认开——
    /// 没装/没开 WE 时不做任何事，保持静态壁纸镜像。</summary>
    public bool DynamicWallpaper { get; set; } = true;

    /// <summary>动态壁纸时禁用图标阴影：图标层走软件光栅化，DropShadow 是帧成本大头
    /// （4K 实测 1031ms→50ms 的差距）。默认开（低配友好）；高配想要阴影可关。</summary>
    public bool DynamicNoShadows { get; set; } = true;

    /// <summary>动态壁纸时禁用布局动画（展开叠放/整理等改为瞬移）：低配机的帧率保底选项。
    /// 默认关（动画是 MacDesk 的灵魂，帧率能接受就留着）。</summary>
    public bool DynamicNoAnimations { get; set; }

    /// <summary>【spike】动态壁纸透明直通：不收编 WE、不建 presenter，把 WPF 窗口表面清除色
    /// 设 Transparent 直接透出下层 WorkerW 里的 WE——图标层保持 GPU 硬件渲染（帧率与静态
    /// 模式一致）。依据 = P0-A"打洞"bug 反证 DefView 子窗表面 alpha 参与 DWM 合成。
    /// 真机验证可行后转正并做成默认路径；不可行则删。settings.json 手写开关，不进 UI。</summary>
    public bool DynamicTransparent { get; set; }

    /// <summary>自启动用计划任务（onlogon 即启）替代 Run 键，绕过 Windows 对启动项的
    /// 串行延迟（机主实测 Run 键要等 ~40s+）。仅记录偏好；实际状态以系统里注册的为准。</summary>
    public bool FastAutostart { get; set; }

    /// <summary>界面语言：auto（跟随系统 UI 语言）| zh | en。重启生效（见 L.cs）。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>选中文件按空格调用第三方预览器（QuickLook/Seer/PowerToys Peek）。默认开——
    /// 没装任何预览器时空格什么都不做（不影响首字母定位，见 FilePreview）。</summary>
    public bool SpacePreview { get; set; } = true;

    /// <summary>空白处右键出 Windows 原生桌面菜单（转发 WM_CONTEXTMENU 给 DefView，Explorer
    /// 弹它自己的现代/经典菜单）；此时按住 Alt 再右键才出 MacDesk 自制菜单。默认关。</summary>
    public bool NativeBackgroundMenu { get; set; }

    /// <summary>图标尺寸（base 图标 DIU，缩放因子 S = IconSize/64）。档位见 MainWindow.IconSizeSteps，
    /// 默认 64。Ctrl +/- 与外观页滑杆调整；不写 Canon（切档=切分辨率同理，仅显示现算）。</summary>
    public int IconSize { get; set; } = 64;

    /// <summary>首行下沉：显示网格整体下移默认档半行（56 DIU），给第三方顶部菜单栏类软件
    /// 让出空间，吸顶窗口不再压住首行图标。纯显示层偏移（见 MainWindow.SinkY），不写 Canon，
    /// 关闭即还原。默认关。</summary>
    public bool FirstRowSink { get; set; }

    /// <summary>渲染方式：auto（默认：检测到老世代 Intel 核显即整进程软渲，见 Gpu.cs）｜
    /// hardware（强制硬件）｜software（强制软件，等效 --soft）。老 Intel 核显的原生 D3D9
    /// 驱动在硬件合成路径把壁纸镜像亮部烧成噪点/白块（issue #1：UHD 620/630 实锤，1:1
    /// 零缩放照烧、最新驱动无效，软件渲染实测干净）。重启生效。</summary>
    public string RenderMode { get; set; } = "auto";

    // ── 原生桌面虚拟图标开关（Windows"桌面图标设置"那一组，方向来自 PR #3 @climashscape）──
    // 首次启动默认跟随原生桌面注册表（HideDesktopIcons\NewStartPanel，1=隐藏；
    // 系统缺省 = 只显示回收站），之后以本设置为准。改动即时生效（RefreshAll 增删图标）。

    public bool ShowRecycleBin { get; set; } = true;
    public bool ShowThisPC { get; set; }
    public bool ShowUserFiles { get; set; }
    public bool ShowNetwork { get; set; }
    public bool ShowControlPanel { get; set; }

    /// <summary>原生桌面当前是否显示某虚拟图标（注册表值缺失时按系统缺省）。</summary>
    private static bool NativeIconShown(string braceGuid, bool defShown)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
            return key?.GetValue(braceGuid) is int hidden ? hidden == 0 : defShown;
        }
        catch { return defShown; }
    }

    private Settings(string file) => _file = file;

    public static Settings Load()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDesk");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        var s = new Settings(file);
        // 虚拟图标默认值先跟原生桌面注册表走；settings.json 里有值再覆盖（首启即所见即所得）
        s.ShowRecycleBin = NativeIconShown("{645FF040-5081-101B-9F08-00AA002F954E}", defShown: true);
        s.ShowThisPC = NativeIconShown("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", defShown: false);
        s.ShowUserFiles = NativeIconShown("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", defShown: false);
        s.ShowNetwork = NativeIconShown("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", defShown: false);
        s.ShowControlPanel = NativeIconShown("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", defShown: false);
        try
        {
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("FreePlacement", out var fp)) s.FreePlacement = fp.GetBoolean();
                if (doc.RootElement.TryGetProperty("MenuBlacklist", out var bl) && bl.ValueKind == JsonValueKind.Array)
                    s.MenuBlacklist = bl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                if (doc.RootElement.TryGetProperty("MenuInMainProcess", out var mm)) s.MenuInMainProcess = mm.GetBoolean();
                if (doc.RootElement.TryGetProperty("AccentColor", out var ac) && ac.ValueKind == JsonValueKind.String)
                    s.AccentColor = ac.GetString()!;
                if (doc.RootElement.TryGetProperty("UseStacks", out var us)) s.UseStacks = us.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicWallpaper", out var dw)) s.DynamicWallpaper = dw.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicNoShadows", out var ns)) s.DynamicNoShadows = ns.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicTransparent", out var dt)) s.DynamicTransparent = dt.GetBoolean();
                if (doc.RootElement.TryGetProperty("DynamicNoAnimations", out var na)) s.DynamicNoAnimations = na.GetBoolean();
                if (doc.RootElement.TryGetProperty("FastAutostart", out var fa)) s.FastAutostart = fa.GetBoolean();
                if (doc.RootElement.TryGetProperty("StackGroupBy", out var gb) && gb.ValueKind == JsonValueKind.String)
                    s.StackGroupBy = gb.GetString()!;
                if (doc.RootElement.TryGetProperty("StackFolders", out var sf) && sf.ValueKind == JsonValueKind.Array)
                    s.StackFolders = sf.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                if (doc.RootElement.TryGetProperty("Language", out var lg) && lg.ValueKind == JsonValueKind.String)
                    s.Language = lg.GetString()!;
                if (doc.RootElement.TryGetProperty("SpacePreview", out var sp)) s.SpacePreview = sp.GetBoolean();
                if (doc.RootElement.TryGetProperty("NativeBackgroundMenu", out var nb)) s.NativeBackgroundMenu = nb.GetBoolean();
                if (doc.RootElement.TryGetProperty("IconSize", out var iz) && iz.ValueKind == JsonValueKind.Number) s.IconSize = iz.GetInt32();
                if (doc.RootElement.TryGetProperty("FirstRowSink", out var rs)) s.FirstRowSink = rs.GetBoolean();
                if (doc.RootElement.TryGetProperty("RenderMode", out var rm) && rm.ValueKind == JsonValueKind.String)
                    s.RenderMode = rm.GetString()!;
                else if (doc.RootElement.TryGetProperty("SoftwareRender", out var sr) && sr.ValueKind == JsonValueKind.True)
                    s.RenderMode = "software"; // ≤v1.3.0 的布尔开关：开过的迁移成强制软件；没开过的走 auto
                if (doc.RootElement.TryGetProperty("ShowRecycleBin", out var v1)) s.ShowRecycleBin = v1.GetBoolean();
                if (doc.RootElement.TryGetProperty("ShowThisPC", out var v2)) s.ShowThisPC = v2.GetBoolean();
                if (doc.RootElement.TryGetProperty("ShowUserFiles", out var v3)) s.ShowUserFiles = v3.GetBoolean();
                if (doc.RootElement.TryGetProperty("ShowNetwork", out var v4)) s.ShowNetwork = v4.GetBoolean();
                if (doc.RootElement.TryGetProperty("ShowControlPanel", out var v5)) s.ShowControlPanel = v5.GetBoolean();
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(
                new { FreePlacement, MenuBlacklist, MenuInMainProcess, AccentColor, UseStacks, StackGroupBy, StackFolders, DynamicWallpaper, DynamicNoShadows, DynamicNoAnimations, DynamicTransparent, FastAutostart, Language, SpacePreview, NativeBackgroundMenu, IconSize, FirstRowSink, RenderMode, ShowRecycleBin, ShowThisPC, ShowUserFiles, ShowNetwork, ShowControlPanel },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
