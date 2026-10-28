// =========================================================================
// LoginWindow：登录窗（View 装配层——密码框双向接线 + 键盘链 + 关窗转发）
//
// V0.0.20 对齐 HJVision mFormLogin 功能清单（外观 Fresh 风保持）：
//   ① 四个 PasswordBox（登录密码 + 改密三框）双向接线：框→VM 事件转交
//     （PasswordBox.Password 不可绑定，官方 workaround），VM→框回写
//     （预填/跟随账号/清空——VM 直设密码后框要跟着显示）。防循环靠
//     "值相同不再走"：回写触发 PasswordChanged 时值已一致不再转交 VM。
//   ② 键盘链对齐 mFormLogin 的 KeyPress/ProcessCmdKey 矩阵（统一在
//     PreviewKeyDown，按钮不用 IsDefault/IsCancel——双面板下会吃错回车）：
//     登录面板：用户名回车→密码框、密码回车=登录、Esc=关窗；
//     改密面板：原密码回车→新密码框→确认框、确认回车=保存、Esc=回登录面板。
//     下拉展开时回车/Esc 放行给 ComboBox（先选完收起，别抢——mFormLogin
//     同款注释语义）。
// MVVM 纪律：VM 与门禁链零改动（LoginName/Level 输出、RequestClose 订阅
//   挂在 DataContextChanged——DialogService 是先建窗后设 DataContext）。
// =========================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CureStation.ViewModels;

namespace CureStation;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not LoginViewModel vm)
                return;
            vm.RequestClose += ok =>
            {
                DialogResult = ok;
                Close();
            };
            // 四个密码框接线：登录密码 + 改密三框（转交 + 回写各一份）。
            Wire(vm, PwdBox, nameof(LoginViewModel.Password),
                v => v.Password, (v, s) => v.Password = s);
            Wire(vm, OldPwdBox, nameof(LoginViewModel.OldPassword),
                v => v.OldPassword, (v, s) => v.OldPassword = s);
            Wire(vm, NewPwdBox, nameof(LoginViewModel.NewPassword),
                v => v.NewPassword, (v, s) => v.NewPassword = s);
            Wire(vm, NewPwd2Box, nameof(LoginViewModel.NewPassword2),
                v => v.NewPassword2, (v, s) => v.NewPassword2 = s);
        };
        // 初始焦点：用户名已有值（回填记住账号）→ 光标直接进密码框省一次 Tab。
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(UserCombo.Text))
                UserCombo.Focus();
            else
                PwdBox.Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// 单个密码框双向接线：框→VM 转交（输入），VM→框 回写（预填/跟随/清空）。
    /// 两侧都以"值不同"为闸——同值不再走，天然防互踢死循环。
    /// </summary>
    private static void Wire(LoginViewModel vm, PasswordBox box, string propName,
        Func<LoginViewModel, string> get, Action<LoginViewModel, string> set)
    {
        box.Password = get(vm);   // 初始同步（构造预填的记住密码）
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == propName && box.Password != get(vm))
                box.Password = get(vm);
        };
        box.PasswordChanged += (_, _) =>
        {
            if (box.Password != get(vm))
                set(vm, box.Password);
        };
    }

    /// <summary>
    /// 键盘链（对齐 mFormLogin：用户名回车跳密码、密码回车=登录、确认回车=保存、
    /// Esc 分面板——改密面板先回登录，登录面板关窗）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape)
            return;
        // 下拉展开时回车/Esc 先让 ComboBox 自己处理（选完收起），别抢。
        if (UserCombo.IsDropDownOpen)
            return;
        if (DataContext is not LoginViewModel vm)
            return;
        var focused = Keyboard.FocusedElement as UIElement;

        if (e.Key == Key.Escape)
        {
            // Esc：改密面板先回登录面板，登录面板直接关窗（同 × / 取消语义）。
            if (vm.IsChangePwd)
                vm.BackCommand.Execute(null);
            else
                vm.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // 回车：按焦点所在框分发（焦点不在任何框上不拦，让默认路由走）。
        if (ReferenceEquals(focused, UserCombo))
        {
            PwdBox.Focus();
            e.Handled = true;
        }
        else if (ReferenceEquals(focused, PwdBox))
        {
            vm.OkCommand.Execute(null);
            e.Handled = true;
        }
        else if (ReferenceEquals(focused, OldPwdBox))
        {
            NewPwdBox.Focus();
            e.Handled = true;
        }
        else if (ReferenceEquals(focused, NewPwdBox))
        {
            NewPwd2Box.Focus();
            e.Handled = true;
        }
        else if (ReferenceEquals(focused, NewPwd2Box))
        {
            vm.SavePwdCommand.Execute(null);
            e.Handled = true;
        }
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

    /// <summary>右上 ×：走 VM 取消命令（与 Esc/取消同一条 RequestClose(false) 链）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.CancelCommand.Execute(null);
    }
}
