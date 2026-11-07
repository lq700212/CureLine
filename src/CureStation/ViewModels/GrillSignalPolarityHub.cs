// =========================================================================
// GrillSignalPolarityHub：光栅信号极性中枢（程序①私有，仿 GrillAlarmResetModeHub）
//
// 干什么：策略窗参数条"光栅信号取反"勾选框的状态真源——一处保存，
//   多窗多线程同步：
//   ① 策略窗勾选/取消 → 落盘 config.ini [setting] GrillSignalPolarity
//      记忆 + 广播 Changed（主窗演示 Mock 等消费方按需跟随）；
//   ② 状态机每拍经 Func<Current> 现取（主窗构造时把 ()=>Current 传给
//      SlotCuringMachine 的 grillPolarityProvider）——门控/报警/复位判定
//      随勾选下一拍生效，无需重启；
//   ③ 重启/开窗 LoadFromIni 回上次选择。
// 为什么静态中枢：极性是程序①全局现场接线口径（不随单条策略保存），
//   状态机（Core 不读 ini）、策略窗 VM、演示 Mock 三方要同拍——静态单例 +
//   事件是最轻握手（先例：GrillAlarmResetModeHub、DurationUnit、ThemeManager）。
//
// 缺省 BlockedOn（遮挡=ON）=V0.0.63~V0.0.71 现行口径，保住既有部署行为；
//   现场光栅"被挡=OFF"（V0.0.72 用户实测）在策略窗勾选"光栅信号取反"切到
//   BlockedOff。缺键/非法值一律回 BlockedOn（安全侧：缺省不改变既有现场
//   判定方向；真要反相必须有人显式勾选，不会被坏配置悄悄翻掉）。
//
// 线程纪律：Change/LoadFromIni 一律 UI 线程调（Changed 订阅方是 VM/演示
//   刷新；状态机 provider 只读 Current——枚举静态字段读写原子，后台读安全）。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;

namespace CureStation.ViewModels;

public static class GrillSignalPolarityHub
{
    /// <summary>光栅信号极性记忆 ini 键（config.ini [setting]，程序①专用 UI 键）。</summary>
    private const string PolarityIniKey = "GrillSignalPolarity";

    private static GrillSignalPolarity _current = ReadIni();

    /// <summary>当前光栅信号极性（切换走 Change，勿直接赋值；状态机可后台线程读）。</summary>
    public static GrillSignalPolarity Current => _current;

    /// <summary>极性切换广播（Change 时触发；供需要即时跟进的消费方/测试订阅——
    /// 状态机与演示 Mock 都走每拍现取 provider，不依赖本事件）。UI 线程触发。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 切换极性（策略窗勾选框唯一入口）：落盘记忆 + 置状态 + 广播。
    /// 收敛在中枢一处——策略窗/主窗不再各自读写 ini，防口径分叉。
    /// </summary>
    public static void Change(GrillSignalPolarity polarity)
    {
        if (polarity == _current)
            return;
        _current = polarity;
        IniFile.Write(AppPaths.ConfigIni, "setting", PolarityIniKey,
            polarity == GrillSignalPolarity.BlockedOff ? "BlockedOff" : "BlockedOn");
        Changed?.Invoke();
    }

    /// <summary>重读记忆极性（测试 TestScope 切临时目录后用；生产启动静态初始化已读一次）。
    /// 缺键/非法值一律回 BlockedOn（现行口径）自愈。</summary>
    public static void LoadFromIni() => _current = ReadIni();

    /// <summary>测试恢复：静默置回快照值（不写盘、不广播）。public 是因测试工程
    /// 无程序① InternalsVisibleTo——生产代码勿调（正常切换走 Change）。</summary>
    public static void RecallForTest(GrillSignalPolarity polarity) => _current = polarity;

    private static GrillSignalPolarity ReadIni()
    {
        string text = IniFile.ReadStr(AppPaths.ConfigIni, "setting", PolarityIniKey,
            nameof(GrillSignalPolarity.BlockedOn));
        return GrillSignalPolarityParser.Parse(text);
    }
}
