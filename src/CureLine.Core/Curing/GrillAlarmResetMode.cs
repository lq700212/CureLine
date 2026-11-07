// =========================================================================
// GrillAlarmResetMode：光栅报警复位模式（V0.0.69，用户拍板人工复位/自动复位）
//
// 干什么：计时中光栅报警（红灯）解除的方式二选一——
//   Auto = 自动复位：光栅让空（双侧全空，见 SlotCuringMachine 复位判定）
//     即自动复位（红灯灭黄灯复亮）；
//   Manual（缺省）= 人工复位：光栅让空不复位（红灯与蜂鸣保持），程序弹出
//     "报警复位"窗列出正在报警的库位，操作员逐项/全部点【解除报警】按钮确认后
//     才解除。
//
// 为什么枚举放 Core：选择 consume 它的是 SlotCuringMachine（状态机复位判定），
//   属 Core 语义；配置读取/落盘在程序①的 GrillAlarmResetModeHub（Core 不读
//   ini——与 DurationUnit 同口径），状态机经构造注入 Func<枚举> 取当前模式。
//
// 怎么改：新增模式档位在这里加枚举值 + Hub 解析核对 + 状态机复位分派处理；
//   缺省 Manual（V0.0.71 用户拍板：现场默认必须人工确认），非法配置值同样
//   回 Manual——配置坏了宁可多确认一次，也不让报警被悄悄自动抹掉。
// =========================================================================

namespace CureLine.Core.Curing;

public enum GrillAlarmResetMode
{
    /// <summary>自动复位：光栅双侧全让空即自动复位（V0.0.64~V0.0.70 的缺省行为）。</summary>
    Auto = 0,
    /// <summary>人工复位（缺省，V0.0.71 起）：光栅让空不复位，等操作员在"报警复位"窗点按钮确认。</summary>
    Manual = 1,
}

/// <summary>
/// ini 值 → 枚举解析（纯函数，任意线程可调）：忽略大小写认 "Auto"，
/// 其余（"Manual"/null/空/拼错/他语言别名）一律回 Manual（缺省档）。
/// 注意与旧版相反（V0.0.70 及以前非法值回 Auto）——V0.0.71 用户拍板
/// "现场默认人工确认"，解析方向随之翻转，勿按旧注释回改。
/// </summary>
public static class GrillAlarmResetModeParser
{
    public static GrillAlarmResetMode Parse(string? raw) =>
        string.Equals(raw?.Trim(), nameof(GrillAlarmResetMode.Auto), StringComparison.OrdinalIgnoreCase)
            ? GrillAlarmResetMode.Auto
            : GrillAlarmResetMode.Manual;
}
