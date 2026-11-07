// =========================================================================
// GrillAlarmResetModeHub：光栅报警复位模式中枢（程序①私有，仿 DurationUnit）
//
// 干什么：策略窗右上"报警复位"按钮（人工复位/自动复位二态）的状态真源——
//   一处保存，多窗多线程同步：
//   ① 策略窗 Change() 切模式 → 落盘 config.ini [setting] GrillAlarmResetMode
//      记忆 + 广播 Changed → 主窗立即重跑报警复位窗判定（已有报警立即弹窗/
//      收窗，不等 500ms 拍）；
//   ② 状态机每拍经 Func<Current> 现取（主窗构造时把 ()=>Current 传给
//      SlotCuringMachine 的 resetModeProvider）——复位判定随模式热切下一拍生效；
//   ③ 重启/开窗 LoadFromIni 回上次选择。
// 为什么静态中枢：模式是程序①全局操作规程（不随单条策略保存），状态机
//   （Core 不读 ini）、策略窗 VM、主窗 VM 三方要同拍——静态单例 + 事件是
//   最轻握手（先例：DurationUnit 单位中枢、ThemeManager 主题中枢）。
//
// 缺省 Manual（V0.0.71 用户拍板：现场不动按钮就按人工复位跑——报警必须由
//   操作员逐库位确认才解除）；缺键/非法值/手工改坏 ini 一律自愈回 Manual
//   （宁可多确认一次，也不让报警被悄悄自动抹掉）。
//
// 线程纪律：Change/LoadFromIni 一律 UI 线程调（Changed 订阅方是 VM 刷新；
//   状态机 provider 只读 Current——枚举静态字段读写原子，后台读安全）。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;

namespace CureStation.ViewModels;

public static class GrillAlarmResetModeHub
{
    /// <summary>复位模式记忆 ini 键（config.ini [setting]，程序①专用 UI 键）。</summary>
    private const string ModeIniKey = "GrillAlarmResetMode";

    private static GrillAlarmResetMode _current = ReadIni();

    /// <summary>当前复位模式（切换走 Change，勿直接赋值；状态机可后台线程读）。</summary>
    public static GrillAlarmResetMode Current => _current;

    /// <summary>模式切换广播（Change 时触发；订阅方=主窗 VM 立即重弹/收报警复位窗）。UI 线程触发。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 切换模式（策略窗按钮唯一入口）：落盘记忆 + 置状态 + 广播。
    /// 收敛在中枢一处——策略窗/主窗不再各自读写 ini，防口径分叉。
    /// </summary>
    public static void Change(GrillAlarmResetMode mode)
    {
        if (mode == _current)
            return;
        _current = mode;
        IniFile.Write(AppPaths.ConfigIni, "setting", ModeIniKey,
            mode == GrillAlarmResetMode.Manual ? "Manual" : "Auto");
        Changed?.Invoke();
    }

    /// <summary>重读记忆模式（测试 TestScope 切临时目录后用；生产启动静态初始化已读一次）。
    /// 缺键/非法值一律回 Manual（V0.0.71 缺省档）自愈。</summary>
    public static void LoadFromIni() => _current = ReadIni();

    /// <summary>测试恢复：静默置回快照值（不写盘、不广播）。public 是因测试工程
    /// 无程序① InternalsVisibleTo——生产代码勿调（正常切换走 Change）。</summary>
    public static void RecallForTest(GrillAlarmResetMode mode) => _current = mode;

    private static GrillAlarmResetMode ReadIni()
    {
        string text = IniFile.ReadStr(AppPaths.ConfigIni, "setting", ModeIniKey,
            nameof(GrillAlarmResetMode.Manual));
        return GrillAlarmResetModeParser.Parse(text);
    }
}
