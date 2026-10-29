// =========================================================================
// IoTestWindow：IO 通讯测试窗（View 装配层——DataContext 由 DialogService 注入）
//
// 关窗即 Detach：停 500ms 刷新计时器、停跑马灯、退订总线事件（防悬挂订阅）；
// 业务全在 IoTestViewModel（共享主窗总线，本窗不建 TCP）。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class IoTestWindow : Window
{
    public IoTestWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IoTestViewModel)?.Detach();
    }

    /// <summary>标题条 ✕（无边框自绘标配；与 Esc/IsCancel 同走 Close → Closed 退订链）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
