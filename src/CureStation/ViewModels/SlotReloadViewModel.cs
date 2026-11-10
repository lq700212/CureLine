// =========================================================================
// SlotReloadViewModel：放回再加工产品时的"固化时长口径"确认弹窗（V0.0.99）
//
// 干什么：中途取出的产品再加工完放回时，**这一盘要固化多久**由操作员拍板：
//   ·【接着之前固化继续固化】= 累计达标口径——它之前已经固化过 N 分钟
//     （例如 8 小时），本盘计划 24 小时，那本盘只需再固化 16 小时，累计正好
//     24 小时达标放行（用户 2026-11-10 拍板口径）；
//   ·【完全重新开始固化】= 前段作废，本盘按完整计划时长从头计；
//   ·【取消】= 本次不放这个产品（扫码流程继续，产品不绑定）。
// 为什么必须问：同一库位一个计时器，计时长度只有一种——同一盘里既有全新件
//   又有续接件必然导致有的固化不足、有的过头。库位口径由**第一个**产品定档，
//   后来者口径不同直接拦截（禁混装）。
// 文案：把"之前固化了多久、这次还要固化多久"直接算给操作员看，不让操作员
//   自己算——现场算错就是质量事故。
// 线程：UI 线程（模态）。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>放回口径确认弹窗 VM（三个决策：续接 / 全新重开 / 取消）。</summary>
public sealed class SlotReloadViewModel : ViewModelBase
{
    /// <summary>操作员的选择结果（窗关掉=取消）。</summary>
    public SlotReloadChoice Choice { get; private set; } = SlotReloadChoice.Cancel;

    /// <summary>产品 SN（正文里显示）。</summary>
    public string ProductSn { get; }

    /// <summary>本次放进去的库位码。</summary>
    public string LocationCode { get; }

    /// <summary>原库位码（说明"不能放回原库位"）。</summary>
    public string PreviousLocationCode { get; }

    /// <summary>之前累计已固化分钟。</summary>
    public int PriorMinutes { get; }

    /// <summary>本盘计划固化时长（分钟；0=未知）。</summary>
    public int PlanMinutes { get; }

    /// <summary>主提示正文（说明发生了什么、要决定什么）。</summary>
    public string Message { get; }

    /// <summary>
    /// 续接后本次还需固化多少分钟（0=不缩短——计划 ≤ 前段则无需再固化）。
    /// </summary>
    public int ResumeRemainMinutes => Math.Max(0, PlanMinutes - PriorMinutes);

    /// <summary>【接着之前固化继续】按钮的说明（把两个数摆出来）。</summary>
    public string ResumeText =>
        $"接着之前固化继续（本次再固化 {ResumeRemainMinutes} 分钟，累计达计划 {PlanMinutes} 分钟）";

    /// <summary>【完全重新开始】按钮的说明。</summary>
    public string FreshText => $"完全重新开始（按完整计划 {PlanMinutes} 分钟从头计）";

    /// <summary>三个决策命令。</summary>
    public RelayCommand ResumeCommand { get; }
    public RelayCommand FreshCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>构造（prompt=Core 组装的问题；close=收窗回调）。</summary>
    public SlotReloadViewModel(SlotReloadPrompt prompt, Action close)
    {
        ProductSn = prompt.ProductSn;
        LocationCode = prompt.LocationCode;
        PreviousLocationCode = prompt.PreviousLocationCode;
        PriorMinutes = prompt.PriorMinutes;
        PlanMinutes = prompt.PlanMinutes;

        Message =
            $"产品 {prompt.ProductSn} 之前在库位 {prompt.PreviousLocationCode} 中途取出，" +
            $"取出时已累计固化 {prompt.PriorMinutes} 分钟。本次放入库位 {prompt.LocationCode}，" +
            "请确认本盘的固化时长口径（同一库位的固化时间必须统一）。";

        ResumeCommand = new RelayCommand(() => { Choice = SlotReloadChoice.Resume; close(); });
        FreshCommand = new RelayCommand(() => { Choice = SlotReloadChoice.Fresh; close(); });
        CancelCommand = new RelayCommand(() => { Choice = SlotReloadChoice.Cancel; close(); });
    }
}
