using MacDesk.Interop;
using Microsoft.Win32;

namespace MacDesk.Services;

/// <summary>
/// 原生桌面图标（SHELLDLL_DefView 里的 SysListView32）的可见性，两个层次：
///  - 会话级：ShowWindow 藏/显（DesktopLayer.SetNativeIconsVisible），进程内立刻生效，
///    Explorer 自己并不知情——它下次重建桌面视图（开机/重启 shell）照样画一遍原生图标；
///  - 持久级：Explorer 自己的「查看 → 显示桌面图标」状态（HKCU\…\Advanced\HideIcons），
///    **建桌面视图时读**，所以开机阶段原生图标压根不会被画出来。
///
/// 用户反馈"电脑刚开机会闪一下原桌面图标"的根因就是我们只做了会话级：Explorer 先画一遍，
/// 等 MacDesk 进程起来、首帧渲染完、挂上桌面层才藏掉（真机日志 ~1s，冷启动更久）。
/// 持久级把这段窗口期整个消灭。
///
/// 持久级走 shell 自己的命令（DefView 的 WM_COMMAND 0x7402 = 切换显示桌面图标），
/// 不直接写注册表：命令路径把 Explorer 进程内状态和注册表一起改掉，两边不分叉
/// （真机遇到过直写注册表后被 Explorer 按旧状态盖回的情况）。命令是「切换」不是「设置」，
/// 所以要先读当前状态、只在需要时发一次，发完复核；复核不过再退回直写注册表。
///
/// 只有**开机自启开着**时才该持久隐藏：否则重启后没人接管，用户对着一张空桌面。
/// </summary>
internal static class NativeIcons
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string HideIconsValue = "HideIcons";
    private const uint WM_COMMAND = 0x0111;
    private const int CmdToggleDesktopIcons = 0x7402; // shell 的"显示桌面图标"命令 id

    /// <summary>Explorer 记住的持久状态：true = 下次建桌面视图时不画原生图标。</summary>
    public static bool PersistentlyHidden
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(AdvancedKey);
                return k?.GetValue(HideIconsValue) is int v && v != 0;
            }
            catch { return false; }
        }
    }

    /// <summary>把「本会话可见性」和「Explorer 持久状态」一起校准到目标（幂等，可反复调用）。
    /// hidden = 本会话要不要藏；persist = 允不允许把"藏"写进 Explorer 的持久状态。</summary>
    public static void Apply(bool hidden, bool persist)
    {
        bool wantPersist = hidden && persist;
        if (PersistentlyHidden != wantPersist)
        {
            // 「本会话仍要藏、只是不再持久」不能走 shell 切换：那会让原生图标真的显出来，
            // 而我们还盖在桌面上（透明直通模式会直接透出来 = 双份图标）。只改注册表。
            if (!wantPersist && hidden) ClearPersistentHide();
            else SetPersistent(wantPersist);
        }
        else Log.Write($"native icons: hidden={hidden} persistent={wantPersist} (already in sync)");
        // 持久状态的切换命令会顺带改可见性，最后再按本会话意图钉一次（幂等）
        DesktopLayer.SetNativeIconsVisible(!hidden);
    }

    /// <summary>只清持久状态、不碰当前会话可见性：「关掉开机自启但 MacDesk 还在跑」时用
    /// ——下次开机没人接管，得让 Explorer 自己把原生图标画回来。</summary>
    public static void ClearPersistentHide()
    {
        if (PersistentlyHidden) WriteRegistry(false, "autostart off; session unchanged");
    }

    private static void SetPersistent(bool hidden)
    {
        var defView = DesktopLayer.DefViewHwnd;
        if (defView == IntPtr.Zero || !Native.IsWindow(defView))
        {
            WriteRegistry(hidden, "no DefView");
            return;
        }
        Native.SendMessageTimeout(defView, WM_COMMAND, new IntPtr(CmdToggleDesktopIcons),
            IntPtr.Zero, Native.SMTO_NORMAL, 3000, out _);
        for (int i = 0; i < 10 && PersistentlyHidden != hidden; i++) Thread.Sleep(50); // 写注册表可能慢半拍
        if (PersistentlyHidden == hidden)
            Log.Write($"native icons: persistent hidden={hidden} (shell toggle)");
        else
            WriteRegistry(hidden, "shell toggle did not land");
    }

    private static void WriteRegistry(bool hidden, string why)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(AdvancedKey);
            k?.SetValue(HideIconsValue, hidden ? 1 : 0, RegistryValueKind.DWord);
            Log.Write($"native icons: persistent hidden={hidden} (registry fallback: {why})");
        }
        catch (Exception ex) { Log.Write($"native icons: persist failed ({why}): {ex.Message}"); }
    }
}
