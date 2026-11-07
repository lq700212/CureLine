// =========================================================================
// GrillSignalPolarity：光栅信号极性（V0.0.72，用户现场实测拍板）
//
// 干什么：定义"光栅被遮挡时 DI 信号是高还是低"——状态机所有光栅读值
//   （门控放行 / 计时中报警 / 自动复位判定 / 重启对齐复核 / 排障日志
//   "L=挡 R=空"）都经本枚举解释，一处切换全局生效。
//
//   BlockedOn（缺省）= 遮挡=ON：光栅被挡时信号 ON、未被挡 OFF。
//     V0.0.63~V0.0.71 的唯一口径，保持缺省以免既有部署/测试行为漂移；
//     单测与 DocShot 演示都按此口径编排。
//   BlockedOff      = 遮挡=OFF：光栅被挡时信号 OFF、未被挡 ON。
//     现场实测口径（常闭安全光栅的正常输出 ON、遮挡断光 OFF），
//     程序①现场需在策略窗光栅条选【常闭】切到本档。
//
// 为什么放 Core：消费方是 SlotCuringMachine（门控/报警判定），属 Core
//   语义；配置读取/落盘在程序①的 GrillSignalPolarityHub（Core 不读 ini，
//   与 GrillAlarmResetMode 同口径），状态机经构造注入 Func<枚举> 现取。
//
// 怎么改：其它品牌光栅（如遮挡输出干接点闭合/断开反接）只在本枚举
//   加档位 + Hub 解析核对 + 状态机 IsBlocked 判定即可，界面分段按钮无需动
//   （程序① UI 只区分【常开/常闭】两态；真要三态再扩 UI）。
// =========================================================================

namespace CureLine.Core.Curing;

public enum GrillSignalPolarity
{
    /// <summary>遮挡=ON（缺省）：光栅被挡时 DI 为 ON、未被挡为 OFF。</summary>
    BlockedOn = 0,

    /// <summary>遮挡=OFF：光栅被挡时 DI 为 OFF、未被挡为 ON（现场实测常闭安全光栅）。</summary>
    BlockedOff = 1,
}

/// <summary>
/// ini 值 → 枚举解析（纯函数，任意线程可调）：忽略大小写只认
/// "BlockedOff"，其余（"BlockedOn"/null/空/拼错/他语言别名）一律回
/// BlockedOn（缺省档）。方向勿翻转——缺省必须保持 V0.0.71 及以前的
/// "遮挡=ON"口径，否则既有部署的光栅判定会整体反相（安全事故级）。
/// </summary>
public static class GrillSignalPolarityParser
{
    public static GrillSignalPolarity Parse(string? raw) =>
        string.Equals(raw?.Trim(), nameof(GrillSignalPolarity.BlockedOff),
            StringComparison.OrdinalIgnoreCase)
            ? GrillSignalPolarity.BlockedOff
            : GrillSignalPolarity.BlockedOn;
}
