using CimGateway.ViewModels;
using System.Windows;
using PlcMesBridge.ViewModels;

namespace CimGateway;

/// <summary>
/// 登录窗（MVVM 的 View：只设 DataContext + 转交密码 + 响应关窗请求）。
/// 逻辑全在 LoginViewModel；DialogService.ShowLogin 经 ViewModel 拿结果。
/// </summary>
public partial class LoginWindow : Window
{
    public LoginViewModel ViewModel { get; }

    public LoginWindow()
    {
        ViewModel = new LoginViewModel();
        DataContext = ViewModel;
        InitializeComponent();
        // 预填/跟随账号时 VM 先有密码，回写到密码框（VM.Password 是源）。
        TxtPwd.Password = ViewModel.Password;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.Password)
                && TxtPwd.Password != ViewModel.Password)
                TxtPwd.Password = ViewModel.Password;
        };
        ViewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(ViewModel.UserName))
                TxtUser.Focus();
            else
                TxtPwd.Focus();
        };
    }

    /// <summary>密码框→VM（Password 非依赖属性，事件转交是官方 workaround）。</summary>
    private void TxtPwd_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box
            && box.Password != ViewModel.Password)
            ViewModel.Password = box.Password;
    }
}
