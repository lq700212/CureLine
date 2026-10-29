// =========================================================================
// OperatorWindow：权限管理窗（View 装配层——DataContext 由 DialogService 注入）
// =========================================================================

using System.Windows;

namespace CureStation;

public partial class OperatorWindow : Window
{
    public OperatorWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题条 ✕（无边框自绘标配）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
