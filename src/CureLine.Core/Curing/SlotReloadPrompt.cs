// =========================================================================
// SlotReloadPrompt / SlotReloadChoice：放回再加工产品时的"固化时长口径"确认
//
// 干什么：中途取出的产品再加工完放回时，**这一盘要固化多久**由操作员拍板：
//   ·【接着之前固化的时长继续固化】= 累计达标口径——它之前已经固化过 N 分钟
//     （例如 8 小时），本盘计划 24 小时，那本盘只需再固化 16 小时，累计正好
//     24 小时达标放行（用户 2026-11-10 拍板口径）；
//   ·【完全重新开始固化】= 前段作废，本盘按完整计划时长从头计。
// 为什么必须是"同一库位口径统一"（用户拍板）：一盘产品一个计时器，计时长度
//   只有一种；混装（库里既有全新件又有续接件）必然导致有的产品固化不足、有的
//   固化过头 → 库位口径由**第一个**产品定档，后来者口径不同直接弹窗拦截。
//   本库位的计时基准 = 续接件"前段已固化时长"的**最小值**（min）——保证库里
//   每个产品的累计固化都 ≥ 计划时长（宁多勿少：固化不足是质量事故，多固化
//   只是多花点时间）。
//
// 线程约定：纯数据/纯枚举，无状态；UI 线程与后台线程都用得上。
// =========================================================================

namespace CureLine.Core.Curing;

/// <summary>操作员对"放回产品怎么算固化时长"的答复。</summary>
public enum SlotReloadChoice
{
    /// <summary>取消：本次不放这个产品（不绑定，流程不变）。</summary>
    Cancel = 0,

    /// <summary>接着之前固化的时长继续固化（累计达标口径）。</summary>
    Resume = 1,

    /// <summary>完全重新开始固化（当全新件对待，前段时长作废）。</summary>
    Fresh = 2,
}

/// <summary>
/// 扫到"有中途取出记录"的产品时，问操作员的那个问题（Core 组装，宿主弹窗）。
/// </summary>
public sealed class SlotReloadPrompt
{
    /// <summary>产品 SN（二维码原文）。</summary>
    public string ProductSn { get; set; } = "";

    /// <summary>本次要放进去的库位显示码（A01~J12）。</summary>
    public string LocationCode { get; set; } = "";

    /// <summary>
    /// 本次要放进去的库位架号（全局架号，点表口径 1~N）。
    /// 为什么带上架层：宿主要按"该库位的计划时长"算"再固化几分钟即达标"，
    /// 而显示码（架内口径 A01~J12）在双架时反解不出全局架号——故直接给。
    /// </summary>
    public int Rack { get; set; }

    /// <summary>本次要放进去的库位层号（1~3）。</summary>
    public int Level { get; set; }

    /// <summary>该产品被取出时的原库位显示码（放回原库位是被禁止的，这里只作说明）。</summary>
    public string PreviousLocationCode { get; set; } = "";

    /// <summary>该产品被取出时**累计**已固化分钟（续接的基准）。</summary>
    public int PriorMinutes { get; set; }

    /// <summary>
    /// 该库位的计划固化时长（分钟）——宿主按当前策略填，供文案展示
    /// "再固化 X 分钟即累计达计划时长"。Core 不知道策略，故留空由宿主补。
    /// </summary>
    public int PlanMinutes { get; set; }
}
