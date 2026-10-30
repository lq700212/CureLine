using CimGateway.ViewModels;
using System.Windows;
using System.Windows.Controls;
using CureLine.ViewModels;

namespace CimGateway;

/// <summary>
/// 修改密码窗（MVVM 的 View：只设 DataContext + 转交密码 + 响应关窗请求）。
/// </summary>
public partial class ChangePasswordWindow : Window
{
    public ChangePasswordViewModel ViewModel { get; }

    public ChangePasswordWindow(string currentUser,
        CureLine.Core.Infrastructure.LoginRole currentRole)
    {
        ViewModel = new ChangePasswordViewModel(currentUser, currentRole);
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };
        Loaded += (_, _) => TxtOld.Focus();
    }

    /// <summary>密码框→VM（Password 非依赖属性，事件转交是官方 workaround）。</summary>
    private void PwdBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender == TxtOld && TxtOld.Password != ViewModel.OldPassword)
            ViewModel.OldPassword = TxtOld.Password;
        else if (sender == TxtNew && TxtNew.Password != ViewModel.NewPassword)
            ViewModel.NewPassword = TxtNew.Password;
        else if (sender == TxtConfirm && TxtConfirm.Password != ViewModel.ConfirmPassword)
            ViewModel.ConfirmPassword = TxtConfirm.Password;
    }

    /// <summary>标题条关闭钮：不设 DialogResult（等价取消，改动不落盘）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
