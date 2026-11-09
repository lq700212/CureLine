// =========================================================================
// DurationUnit：固化时长"显示单位"中枢（策略窗 ↔ 主窗共享，程序①私有）
//
// 干什么：策略窗右上"单位"下拉（时/分）的状态真源——一处保存，两窗同步：
//   ① 策略窗 Change() 切单位 → 落盘 config.ini [setting] PolicyTimeUnit 记忆
//      + 广播 Changed → 主窗立即按新单位重写卡片/统计条的时长文本（不等 500ms 拍）；
//   ② 重启/开窗时 LoadFromIni（或取静态 Current）回上次选择；
//   ③ Text/SplitBig 两个纯格式化函数供主窗拼文本——存储、状态机计时、
//      过账报文恒为分钟，本类只决定"多少分钟用文字怎么说"。
// 为什么做静态中枢：单位是程序①全局 UI 口径，策略窗与主窗两个独立 VM 要同拍
//   （用户拍板"策略里切了，主界面也要同步"）；静态单例 + 事件是两 VM 之间最轻
//   的握手方式（先例：CuringMesPosterHolder 静态桥接、ThemeManager 静态主题）。
//
// 秒档删除（用户拍板）：没有秒级的固化工艺，PolicyTimeUnit 只留 时/分 两档——
//   ini 存量 "Seconds" 值不在新枚举内，LoadFromIni 非法值自愈回缺省 Hours。
//
// 缺省单位=时（2026-11-09 用户拍板）：缺键/空值/非法值一律回 Hours——开箱即
//   小时口径；现场显式写 Minutes 不受影响（读盘为准，缺省只兜兜底）。
//
// 线程纪律：Change/LoadFromIni/恢复 一律 UI 线程调（Changed 订阅方是 VM 集合
//   刷新，与主窗 500ms 拍同线程口径）；Text/SplitBig 纯函数任意线程可调。
// =========================================================================

using System.Globalization;
using CureLine.Core.Infrastructure;

namespace CureStation.ViewModels;

/// <summary>
/// 固化时长显示单位（仅界面展示/输入口径；存储与状态机恒为分钟）。
/// 顺序即下拉顺序：时 → 分，TimeUnitIndex 直接取整数值。
/// </summary>
public enum PolicyTimeUnit
{
    Hours = 0,
    Minutes = 1,
}

public static class DurationUnit
{
    /// <summary>单位记忆 ini 键（config.ini [setting]，程序①专用 UI 键）。</summary>
    private const string TimeUnitIniKey = "PolicyTimeUnit";

    private static PolicyTimeUnit _current = ReadIni();

    /// <summary>当前显示单位（切换走 Change，勿直接赋值）。</summary>
    public static PolicyTimeUnit Current => _current;

    /// <summary>单位切换广播（Change 时触发；订阅方=主窗 VM 立即重刷时长文本）。UI 线程触发。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 切换单位（策略窗唯一入口）：落盘记忆 + 置状态 + 广播。
    /// 收敛在 Hub 一处——策略窗/主窗不再各自读写 ini，防口径分叉。
    /// </summary>
    public static void Change(PolicyTimeUnit unit)
    {
        if (unit == _current)
            return;
        _current = unit;
        IniFile.Write(AppPaths.ConfigIni, "setting", TimeUnitIniKey, unit.ToString());
        Changed?.Invoke();
    }

    /// <summary>
    /// 重读记忆单位（测试 TestScope 切临时目录后用；生产启动静态初始化已读一次）。
    /// 缺键/非法值/旧版 "Seconds"（档已删）一律回缺省 Hours 自愈（缺省单位=时，
    /// 2026-11-09 用户拍板；显式 Minutes 在盘中不受影响）。
    /// </summary>
    public static void LoadFromIni() => _current = ReadIni();

    /// <summary>测试恢复：静默置回快照值（不写盘、不广播）。public 是因测试工程
    /// 无程序① InternalsVisibleTo——生产代码勿调（正常切换走 Change）。</summary>
    public static void RecallForTest(PolicyTimeUnit unit) => _current = unit;

    private static PolicyTimeUnit ReadIni()
    {
        string text = IniFile.ReadStr(AppPaths.ConfigIni, "setting", TimeUnitIniKey,
            nameof(PolicyTimeUnit.Hours));
        return Enum.TryParse<PolicyTimeUnit>(text, ignoreCase: true, out var unit) &&
               Enum.IsDefined(unit)
            ? unit
            : PolicyTimeUnit.Hours;
    }

    // ---------- 分钟数 → 显示文本（纯函数） ----------
    //
    // 小时口径规则（用户拍板"智能省略零段"）：
    //   ≥60 且有余 → "X小时Y分"（1小时28分）；整小时 → "X小时"（2小时）；
    //   不足 1 小时 → "Y分"（47分）；0 → "0分"（不变量兜底）。
    // 分钟口径：保持旧显示（大数字纯数字 + "分"后缀、副文"N 分"）。

    /// <summary>
    /// 带单位整串（状态行"剩 X"、统计条"剩 X"、副文中段）。
    /// 分钟口径带"分"；小时口径按"X小时Y分/…/Y分"整串。
    /// </summary>
    public static string Text(int minutes) => Current == PolicyTimeUnit.Hours
        ? HourText(minutes)
        : $"{minutes}分";

    /// <summary>
    /// 日志版时长文本（状态机日志经宿主 MainViewModel 注入本函数，跟随单位）：
    /// 小时口径同 Text（"24小时"/"1小时28分"/"47分"智能省略零段）；
    /// 分钟口径保持"X 分钟"老字样——与历史日志观感一致（"1440 分钟"，非"1440分"）。
    /// 纯函数任意线程可调（状态机后台线程逐条调，只做 quotients 与字符串拼装）。
    /// </summary>
    public static string LogText(int minutes) => Current == PolicyTimeUnit.Hours
        ? HourText(minutes)
        : $"{minutes} 分钟";

    /// <summary>
    /// 卡片大数字拆"数字段/单位字"交替四段（V0.0.56 二轮：数字 26 号、单位字
    /// 11 号——与分钟模式"分"后缀同字号，用户拍板）：
    ///   分/47 → ("47","分","","")；小时 120 → ("2","小时","","")；
    ///   小时 88 → ("1","小时","28","分")；不足 1 小时 47 → ("47","分","","")。
    /// 空段一律空串（界面显隐由第二组非空推导）。
    /// </summary>
    public static (string Num1, string Unit1, string Num2, string Unit2) SplitBig(int minutes)
    {
        if (Current != PolicyTimeUnit.Hours)
            return (minutes.ToString(CultureInfo.InvariantCulture), "分", "", "");
        if (minutes <= 0)
            return ("0", "分", "", "");
        int hours = minutes / 60, rest = minutes % 60;
        if (hours == 0)
            return (rest.ToString(CultureInfo.InvariantCulture), "分", "", "");
        return rest > 0
            ? (hours.ToString(CultureInfo.InvariantCulture), "小时",
               rest.ToString(CultureInfo.InvariantCulture), "分")
            : (hours.ToString(CultureInfo.InvariantCulture), "小时", "", "");
    }

    /// <summary>小时口径整串："X小时Y分" / 整小时"X小时" / 不足 1 小时"Y分"。</summary>
    private static string HourText(int minutes)
    {
        if (minutes <= 0)
            return "0分";
        int hours = minutes / 60, rest = minutes % 60;
        if (hours == 0)
            return $"{rest}分";
        return rest > 0 ? $"{hours}小时{rest}分" : $"{hours}小时";
    }
}
