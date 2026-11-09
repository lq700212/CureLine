// =========================================================================
// ModalForegroundGuard 测试：模态弹窗"切后台再切回自动找回"的选窗判据。
// 判据是纯函数（快照 record，不依赖 WPF 窗口实例），六场景锁语义：
//   无模态不动作 / 单层模态找回 / 多层模态取最上层 / 下层弹窗被禁跳过 /
//   隐藏窗跳过 / 无 enabled 候选防炸。
// 机制与 WPF 模态语义（ShowDialog 禁用本应用全部其它窗口）见
//   src/CureLine.Core/Infrastructure/ModalForegroundGuard.cs 头注释。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureLine.Tests;

public class ModalForegroundGuardTests
{
    private static ModalForegroundGuard.WinSnapshot Snap(bool isMain, bool isVisible, bool isEnabled)
        => new(IsMain: isMain, IsVisible: isVisible, IsEnabled: isEnabled);

    [Fact(DisplayName = "无模态在跑（主窗 enabled）→ 不动作")]
    public void NoModal_Returns_None()
    {
        // 只有主窗：无 ShowDialog，切回前台是主窗正常行为
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: true, isEnabled: true),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "非模态窗开着但无模态 → 不动作（非模态零误伤）")]
    public void NonModalOnly_Returns_None()
    {
        // 日志/报警/报警复位窗等非模态窗开着：主窗仍 enabled，护栏整体不动作
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: true, isEnabled: true),
            Snap(isMain: false, isVisible: true, isEnabled: true),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "单层模态弹窗 → 返回弹窗索引")]
    public void SingleModal_Returns_Modal()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: true, isEnabled: false),   // 主窗被 ShowDialog 禁用
            Snap(isMain: false, isVisible: true, isEnabled: true),   // 模态弹窗（唯一 enabled）
        };
        Assert.Equal(1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "多层模态 → 取最上层（集合序=打开序，最后打开的）")]
    public void StackedModals_Returns_Top()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: true, isEnabled: false),   // 主窗
            Snap(isMain: false, isVisible: true, isEnabled: false),  // 下层弹窗（被上层禁）
            Snap(isMain: false, isVisible: true, isEnabled: true),   // 最上层弹窗（真正接客层）
        };
        Assert.Equal(2, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "模态期间隐藏的非模态窗跳过（IsVisible=false 不拉起）")]
    public void HiddenWindow_Skipped()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: true, isEnabled: false),
            Snap(isMain: false, isVisible: false, isEnabled: true),  // 隐藏窗（不可见不拉）
            Snap(isMain: false, isVisible: true, isEnabled: true),   // 可见模态弹窗
        };
        Assert.Equal(2, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "主窗被禁但无 enabled 候选 → 不动作（异常态防炸）")]
    public void NoEnabledCandidate_Returns_None()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: true, isEnabled: false),
            Snap(isMain: false, isVisible: false, isEnabled: false),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }

    [Fact(DisplayName = "主窗隐藏态（极罕见）→ 不动作")]
    public void MainHidden_Returns_None()
    {
        var wins = new List<ModalForegroundGuard.WinSnapshot>
        {
            Snap(isMain: true, isVisible: false, isEnabled: false),  // 主窗不可见（判据要求可见被禁）
            Snap(isMain: false, isVisible: true, isEnabled: true),
        };
        Assert.Equal(-1, ModalForegroundGuard.PickRestoreIndex(wins));
    }
}
