// =========================================================================
// LogWindow：日志/控制台悬浮窗（方案 D，2026-10-29）
//   View 只做装配：DataContext/Present 定位/关窗退订——业务收在
//   LogWindowViewModel（Written→无锁入队→100ms 批量搬运）。
//   非模态：DialogService.ShowLog 单例管理（Owner=主窗，重复调用置前）。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class LogWindow : Window
{
    private LogWindowViewModel? _vm;

    public LogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 展示（非模态）：换新 VM（重订日志流）+ 首开右下角定位；已可见则只
    /// 换快照并返回（置前激活由调用方 DialogService.Activate 负责）。
    /// </summary>
    public void Present(LogSystemSnapshot snapshot)
    {
        // 旧 VM 退订（若有）——每次展示换新 VM，绝不悬挂双订阅
        if (_vm != null)
        {
            _vm.Detach();
            Closed -= OnClosedDetach; // 防多次 Present 叠加订阅同一退订回调
        }
        _vm = new LogWindowViewModel(snapshot);
        DataContext = _vm;
        Closed += OnClosedDetach;

        if (!IsVisible)
        {
            PositionBottomRight();
            Show();
        }
    }

    /// <summary>关窗必退订（悬挂订阅=内存泄漏 + 幽灵重复入队，见 VM 注释红线）。</summary>
    private void OnClosedDetach(object? sender, EventArgs e) => _vm?.Detach();

    /// <summary>标题条 ✕（无边框自绘标配；单例生命周期由 Closed 回调接管）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>屏幕工作区右下角定位（不加记忆位置的 ini 键——拍板口径）。</summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }
}
