// =========================================================================
// WorkOrderWindow：工单信息窗（View 装配层——DataContext 由 DialogService 注入）
// =========================================================================

using System.Windows;

namespace CureStation;

public partial class WorkOrderWindow : Window
{
    public WorkOrderWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题条 ✕（无边框自绘标配；ShowDialog 返回值不受影响——保存走命令）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
