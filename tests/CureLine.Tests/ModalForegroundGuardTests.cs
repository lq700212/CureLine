// =========================================================================
// ModalForegroundGuard 测试：模态弹窗"切后台再切回自动找回"的选窗判据。
// 判据是纯函数（快照 record，不依赖 WPF 窗口实例），场景锁语义：
//   无模态不动作 / 非模态零误伤 / 单层模态找回 / 多层模态取最上层 /
//   隐藏窗跳过 / 无候选防炸 / 仅隐藏被禁窗不动作 / 判据不锚定主窗。
// 机制与 WPF 模态语义（ShowDialog 禁用本应用全部其它窗口）见
//   src/CureLine.Core/Infrastructure/ModalForegroundGuard.cs 头注释。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureLine.Tests;

public class ModalForegroundGuardTests
{
    private static ModalForegroundGuard.WinSnapshot Snap(bool isVisible, bool isEnabled)
        => new(IsVisible: isVisible, IsEnabled: isEnabled);

    [Fact(DisplayName = "无模态在跑（全部窗 enabled）→ 不动作")]
    public void NoModal_Returns_None()
    {
        // 只有主窗：无 ShowDialog，切回前台是主窗正常行为
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: true),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "非模态窗开着但无模态 → 不动作（非模态零误伤）")]
    public void NonModalOnly_Returns_None()
    {
        // 日志/报警/报警复位窗等非模态窗开着：无可见被禁窗，护栏整体不动作
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: true),
            Snap(isVisible: true, isEnabled: true),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "单层模态弹窗 → 返回弹窗索引")]
    public void SingleModal_Returns_Modal()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: false),   // 主窗被 ShowDialog 禁用
            Snap(isVisible: true, isEnabled: true),    // 模态弹窗（唯一 enabled）
        };
        Assert.Equal(1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "多层模态 → 取最上层（集合序=打开序，最后打开的）")]
    public void StackedModals_Returns_Top()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: false),   // 主窗
            Snap(isVisible: true, isEnabled: false),   // 下层弹窗（被上层禁）
            Snap(isVisible: true, isEnabled: true),    // 最上层弹窗（真正接客层）
        };
        Assert.Equal(2, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "模态期间隐藏的非模态窗跳过（IsVisible=false 不拉起）")]
    public void HiddenWindow_Skipped()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: false),
            Snap(isVisible: false, isEnabled: true),   // 隐藏窗（不可见不拉）
            Snap(isVisible: true, isEnabled: true),    // 可见模态弹窗
        };
        Assert.Equal(2, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "模态在跑但无 enabled 候选 → 不动作（异常态防炸）")]
    public void NoEnabledCandidate_Returns_None()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: false),
            Snap(isVisible: false, isEnabled: false),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "仅隐藏+被禁窗（无可见被禁窗）→ 不动作")]
    public void OnlyHiddenDisabled_Returns_None()
    {
        // 隐藏的被禁窗不算"有模态在跑"的可见证据；可见窗全 enabled 时
        // 前台本来就是能接客的窗，不强行抢（极罕见快照防御）
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: false, isEnabled: false),  // 隐藏+被禁（不触发）
            Snap(isVisible: true, isEnabled: true),    // 可见弹窗 enabled
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    // V0.0.92 放宽：判据从"主窗被禁"放宽为"任一可见窗被禁"，不再锚定主窗身份
    // ——Application.MainWindow 随激活漂移（模态弹窗打开后指向弹窗），旧实现把
    // 弹窗误标成"主窗"后它 enabled、主窗判据永假，护栏不动作（用户实测
    // Win+D → 任务视图点主窗切回复现）。快照判据只看"有没有可见被禁窗"。
    [Fact(DisplayName = "判据不锚定主窗：任一可见被禁窗即触发")]
    public void AnyVisibleDisabled_Triggers()
    {
        // 集合序=打开序：非模态日志窗先开、主窗第二、模态弹窗最后——
        // 被禁窗不在首位也照常触发，enabled 候选取最后一个
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isVisible: true, isEnabled: true),    // 日志窗（非模态）
            Snap(isVisible: true, isEnabled: false),   // 主窗被 ShowDialog 禁用
            Snap(isVisible: true, isEnabled: true),    // 模态弹窗
        };
        Assert.Equal(2, ModalForegroundGuard.PickRestoreIndex(wins));
    }
}
