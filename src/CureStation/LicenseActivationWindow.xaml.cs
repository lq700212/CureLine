// =========================================================================
// LicenseActivationWindow：软件激活窗（程序① View 层）
//
// 干什么：把 VM 的 RequestClose 转发成 DialogResult/Close；复制设备码到剪贴板。
// 为什么剪贴板放 View：Clipboard 是 UI 设施（VM 纪律：不碰 Window/Clipboard），
//   且单元测试不实例化本窗。
// 怎么改：窗内交互新增一率放这里（业务规则在 LicenseActivationViewModel）。
// =========================================================================

using System.Windows;
using System.Windows.Input;
using CureLine.ViewModels;

namespace CureStation;

public partial class LicenseActivationWindow : Window
{
    public LicenseActivationWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LicenseActivationViewModel vm)
            {
                // 关窗链：激活成功 true / 取消 false（与登录窗同款 RequestClose 模式）。
                vm.RequestClose += ok =>
                {
                    DialogResult = ok;
                    Close();
                };
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Esc=关闭（不激活）；Ctrl+Enter=提交激活（少一次鼠标）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not LicenseActivationViewModel vm)
            return;
        if (e.Key == Key.Escape)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.ActivateCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>复制设备码（空设备码时给提示，不静默无反应）。</summary>
    private void OnCopyDeviceCode(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LicenseActivationViewModel vm)
            return;
        if (string.IsNullOrWhiteSpace(vm.DeviceCode))
        {
            MessageBox.Show("设备码为空（指纹采集失败），请检查系统 WMI/注册表权限后重启程序。",
                "软件激活", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Clipboard.SetText(vm.DeviceCode);
            DeviceCodeBox.SelectAll();
            DeviceCodeBox.Copy();
        }
        catch (Exception ex)
        {
            // 剪贴板被其他程序占用等——原样报原因，方便现场排查
            MessageBox.Show("复制失败：" + ex.Message, "软件激活",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>右上 ×：走 VM 取消命令（与 Esc 同一条 RequestClose(false) 链）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LicenseActivationViewModel vm)
            vm.CloseCommand.Execute(null);
        else
            Close();
    }
}
