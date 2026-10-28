// =========================================================================
// LoginWindow：登录窗（View 装配层——PasswordBox 事件转交 + 关窗转发 + 键盘链）
//
// V0.0.20 重做外观（布局参考 HJVision 登录页、配色按本项目 Fresh 风）：
//   无边框窗新增三件事——标题条拖动（DragMove）、右上 × 关闭（走 CancelCommand
//   保持 VM 语义统一）、用户名回车跳密码框（键盘链：密码框回车由 IsDefault
//   登录按钮接管，Esc 由 IsCancel 取消按钮接管）。
// MVVM 纪律：VM 与门禁链零改动（LoginName/Level 输出、OkCommand/CancelCommand、
//   RequestClose 订阅挂在 DataContextChanged——DialogService 是先建窗后设
//   DataContext，订阅时机不能挪到构造函数）。
// =========================================================================

using System.Windows;
using System.Windows.Input;
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
        // 键盘链：用户名框回车 → 焦点进密码框（密码框回车走 IsDefault 登录）
        UserBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                PwdBox.Focus();
                e.Handled = true;
            }
        };
    }

    /// <summary>标题条拖动移动窗体（无边框窗标配；DragMove 只在按下中合法，兜底吞异常）。</summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已释放等边缘场景：静默忽略（拖不动而已，无功能影响）
            }
        }
    }

    /// <summary>右上 ×：走 VM 取消命令（与 Esc/取消按钮同一条 RequestClose(false) 链）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.CancelCommand.Execute(null);
    }
}
