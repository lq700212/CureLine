// =========================================================================
// LoginWindow：登录窗（View 装配层——PasswordBox 事件转交 + 关窗转发）
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        PwdBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
                vm.Password = PwdBox.Password;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
                vm.RequestClose += ok =>
                {
                    DialogResult = ok;
                    Close();
                };
        };
    }
}
