// =========================================================================
// ScannerConfigWindow：扫码枪配置窗（View 装配层，dev 开发者专属）
//
// 干什么：只做 View 装配——DataContext 由 DialogService 注入；保存/校验/
//   落盘全在 ScannerConfigViewModel（可测）。这里只有两件 View 关切：
//   ① 把 VM 的 RequestClose 转成 DialogResult 关窗（与策略窗同款链：
//      ShowDialog 返回 true，主窗才热重建扫码枪并提示"已保存"）；
//   ② 标题条 ✕ / 取消按钮只关窗不保存。
// 为什么订阅 DataContextChanged：DialogService 是先建窗后设 DataContext，
//   构造时还拿不到 VM（策略窗同款做法）。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class ScannerConfigWindow : Window
{
    public ScannerConfigWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScannerConfigViewModel vm)
                vm.RequestClose += ok =>
                {
                    DialogResult = ok;
                    Close();
                };
        };
    }

    /// <summary>标题条 ✕ / 取消：只关窗不保存（保存走 VM 命令链）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
