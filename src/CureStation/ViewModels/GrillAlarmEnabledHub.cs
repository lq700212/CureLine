// =========================================================================
// GrillAlarmEnabledHub：光栅报警总开关中枢（程序①私有，仿 GrillSignalPolarityHub）
//
// 干什么：策略窗光栅带"光栅报警"开关（V0.0.90 用户拍板"关闭则不报警，
//   默认开启"）的状态真源——一处保存，多窗多线程同步：
//   ① 策略窗切【开关】→ 落盘 config.ini [setting] GrillAlarmEnabled 记忆
//      + 广播 Changed（主窗收到立即收敛现存报警：广播链 Task.Run 调状态机
//      ApplyAlarmSwitch，红灯当场灭、报警复位窗清单即清，不等心跳拍）；
//   ② 状态机每拍经 Func<bool> 现取（主窗构造时把 ()=>Current 传给
//      SlotCuringMachine 的 grillAlarmEnabledProvider）——新报警进进门、
//      已挂的收场，随开关下一拍生效，无需重启；
//   ③ 重启/开窗 LoadFromIni 回上次记忆。
// 为什么静态中枢：报警总开关是程序①全局现场口径（不随单条策略保存），
//   状态机（Core 不读 ini）、策略窗 VM、主窗 VM 三方要同拍——静态单例 +
//   事件是最轻握手（先例：GrillAlarmResetModeHub、GrillSignalPolarityHub）。
//
// 缺省 true（开启）=保住既有现场行为（缺键/空值/非法值/手工改坏 ini 一律
//   自愈回开——"宁可多报不可漏报"，安全侧：静默只缺显示，静默丢报警可能
//   漏事故；真要关报警必须有人显式切【关】）。存储用 "1"/"0" 文本（Simulate
//   同款布尔 ini 惯例），任何非 "0" 文本按开处理。
//
// 语义边界：开关只关"报警"（红灯/蜂鸣/塔灯红/复位窗），门控闸（托盘到位
//   要光栅让空才开计时）是工艺防呆，不受影响——SlotCuringMachine 类头
//   "报警总开关"节。
//
// 线程纪律：Change/LoadFromIni 一律 UI 线程调（Changed 订阅方是 VM/主窗
//   收敛链）；状态机 provider 只读 Current——bool 静态字段读写原子，
//   后台读安全。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureStation.ViewModels;

public static class GrillAlarmEnabledHub
{
    /// <summary>报警总开关记忆 ini 键（config.ini [setting]，程序①专用 UI 键）。</summary>
    private const string EnabledIniKey = "GrillAlarmEnabled";

    private static bool _current = ReadIni();

    /// <summary>当前报警总开关（切换走 Change，勿直接赋值；状态机可后台线程读）。</summary>
    public static bool Current => _current;

    /// <summary>开关切换广播（Change 时触发；订阅方=主窗 VM 立即收敛现存报警）。
    /// UI 线程触发。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 切换报警总开关（策略窗 ToggleButton 唯一入口）：落盘记忆 + 置状态 + 广播。
    /// 收敛在中枢一处——策略窗/主窗不再各自读写 ini，防口径分叉。
    /// </summary>
    public static void Change(bool enabled)
    {
        if (enabled == _current)
            return;
        _current = enabled;
        IniFile.Write(AppPaths.ConfigIni, "setting", EnabledIniKey,
            enabled ? "1" : "0");
        Changed?.Invoke();
    }

    /// <summary>重读记忆开关（测试 TestScope 切临时目录后用；生产启动静态初始化
    /// 已读一次）。缺键/空值/非 "0" 一律回 true（开启，缺省档）自愈。</summary>
    public static void LoadFromIni() => _current = ReadIni();

    /// <summary>测试恢复：静默置回快照值（不写盘、不广播）。public 是因测试工程
    /// 无程序① InternalsVisibleTo——生产代码勿调（正常切换走 Change）。</summary>
    public static void RecallForTest(bool enabled) => _current = enabled;

    /// <summary>读记忆键：显式 "0"（含周围空白）=关，其余（缺省/空/1/别的）=开。</summary>
    private static bool ReadIni()
    {
        string text = IniFile.ReadStr(AppPaths.ConfigIni, "setting", EnabledIniKey,
            "1");
        return text.Trim() != "0";
    }
}
