// =========================================================================
// ThemeManager：程序①双主题切换中枢（V0.0.21 UI 双主题实施）
//
// 干什么：在 Light（G2 浅色渐变浸染）与 Dark（G1 深空玻璃舱）两套资源字典
//   之间切换——两字典语义键同名异值，切字典即全窗换色（DynamicResource 跟随）。
// 为什么这样做：主窗 + 日志悬浮窗全键化用语义 Brush 键（Brush.*），窗体
//   XAML 零主题分支；弹窗维持浅色靠 Fresh.* 固定键（见 App.xaml 注释，
//   第 2 期再让弹窗跟主题）。主题名落盘 config.ini [setting] Theme
//   （Dark/Light，缺省 Light、非法回 Light——ParseTheme 纯函数可测）。
//
// 怎么改：新增主题 = Themes/ 下加同名键 ResourceDictionary + 这里登记 URI；
//   键名勿改——主窗/LogWindow/XAML 触发器全部按键名取色（改键名要同步
//   两字典 + 所有 DynamicResource 引用点）。
//
// 线程纪律：切主题必须在 UI 线程调（动 Application.Current.Resources）；
//   ParseTheme 纯函数任意线程可调。
// =========================================================================

using System.Windows;

namespace CureStation.Themes;

public static class ThemeManager
{
    /// <summary>主题字典路径（按名拼 URI；新增主题在此登记）。</summary>
    private const string DictUriFormat = "/Themes/{0}.xaml";

    /// <summary>当前主题名（"Light"/"Dark"；App.OnStartup 时按 ini 初始化）。</summary>
    public static string Current { get; private set; } = "Light";

    /// <summary>缺省主题（现场无配置时落 Light——G2 浅色为出厂基调）。</summary>
    public const string DefaultTheme = "Light";

    /// <summary>
    /// 主题名规范化（纯函数，任意线程可调、供测试直接断言）：
    /// "Dark"（忽略大小写/首尾空白）→ "Dark"；其它（null/空/未知）→ "Light"。
    /// 非法值消费侧兜底 Light，现场改坏 ini 不至于黑屏。
    /// </summary>
    public static string ParseTheme(string? raw) =>
        string.Equals(raw?.Trim(), "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";

    /// <summary>切换目标名（按钮文案用：当前 Light → 目标 Dark，反之亦然）。</summary>
    public static string ToggleTarget => Current == "Dark" ? "Light" : "Dark";

    /// <summary>
    /// 应用主题：替换 Application.Current.Resources.MergedDictionaries[0]。
    /// 所有 DynamicResource 引用（含 Style Setter / 触发器 Setter）随之全窗生效。
    /// </summary>
    /// <remarks>
    /// 测试态防御：xUnit 里 Application.Current 为 null——只记账不碰资源
    /// （主题落盘语义由 ParseTheme + 调用方写 ini 覆盖，不在此处做 IO）。
    /// </remarks>
    public static void ApplyTheme(string name)
    {
        name = ParseTheme(name);
        Current = name;
        var app = Application.Current;
        if (app == null)
            return; // 测试态 / 无 Application 宿主：只记当前名，不加载字典

        var dict = new ResourceDictionary
        {
            Source = new Uri(string.Format(DictUriFormat, name), UriKind.Relative),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
            merged.Add(dict);
        else
            merged[0] = dict; // 语义键同名覆盖——Fresh.* 固定键在 App.xaml 主字典不受影响
    }
}
