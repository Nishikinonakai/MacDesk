namespace MacDesk.Services;

/// <summary>双语文案（settings.json `Language`: auto | zh | en，设置页可选）。
/// 语言在进程启动时解析一次、运行期不变——堆分组名等既是显示文案又是运行期字典键，
/// 且设置窗/菜单文案分散在一次性构建里，热切换收益低风险高；换语言 = 重启生效
/// （与"菜单在主进程弹出"同口径）。所有用户可见文案走 T(zh, en)；日志保持中文（开发者向）。</summary>
internal static class L
{
    public static bool Zh { get; private set; } = true;

    /// <summary>App.OnStartup 最早调用（所有进程模式，含 menuhost 子进程——降级菜单文案在主进程
    /// 构建，但保持口径统一）。auto = 跟随系统 UI 语言。</summary>
    public static void Init(string setting) => Zh = setting switch
    {
        "zh" => true,
        "en" => false,
        _ => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh",
    };

    public static string T(string zh, string en) => Zh ? zh : en;
}
