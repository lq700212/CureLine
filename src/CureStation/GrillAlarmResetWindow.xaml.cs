// =========================================================================
// GrillAlarmResetWindow：光栅报警"人工复位"悬浮窗（V0.0.69，程序①）
//   View 只做装配：DataContext（编排在 GrillAlarmResetCoordinator，清单热更/
//   复位派发全在它）+ Present 定位 + 关窗表态转发——业务零代码。
//   非模态单例：DialogService.ShowGrillAlarmReset 管理（Owner=主窗，重复调用置前）。
//   关窗语义：用户 ✕=记"已表态"（coordinator.NotifyWindowClosed，不再连环重弹
//   挂着的旧报警）；程序化收窗（CloseGrillAlarmReset，全解除/切自动模式）不算
//   用户表态——SilentClose 标志区分，防把"系统收窗"误记成"用户点 ✕"。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class GrillAlarmResetWindow : Window
{
    /// <summary>程序化收窗标志（CloseGrillAlarmReset→SilentClose 置位——
    /// 系统收窗不记"用户已表态"；用户点 ✕/Alt+F4 时该标志未被置位）。</summary>
    private bool _silentClose;

    public GrillAlarmResetWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 展示（非模态）：换绑编排器（正常为同一引用——热更路上 presenter 不变，
    /// 防御性换绑成本为零）+ 首开右下角定位；已可见则只换绑并返回（置前激活
    /// 由调用方 DialogService 负责）。
    /// </summary>
    public void Present(GrillAlarmResetCoordinator presenter)
    {
        DataContext = presenter;
        if (!IsVisible)
        {
            PositionBottomRight();
            Show();
        }
    }

    /// <summary>程序化收窗（CloseGrillAlarmReset 用）：置标志后关闭，Closed 不记表态。</summary>
    public void SilentClose()
    {
        _silentClose = true;
        Close();
    }

    /// <summary>关窗收口：除非程序化收窗（_silentClose），一律记"用户已表态"——
    /// 防用户点 ✕ 后下一拍 Refresh 又立刻弹回（弹窗开关决策在 coordinator 小账本）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_silentClose && DataContext is GrillAlarmResetCoordinator presenter)
            presenter.NotifyWindowClosed();
        _silentClose = false; // 复位标志（单例复用下个回合）
    }

    /// <summary>标题条 ✕（无边框自绘标配；单例生命周期由 Closed 回调接管）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>屏幕工作区右下角定位（不加记忆位置的 ini 键——日志窗同款拍板口径）。</summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }
}
