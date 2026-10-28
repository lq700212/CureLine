// =========================================================================
// IDialogService：程序①自己的对话框出口（VM 开窗/提示唯一出口，MVVM 可测性关键）
//
// 干什么：VM 不许直接 new Window/MessageBox（那样 VM 就测不了）——
//   一律走本服务；测试用 FakeDialogService 桩（tests 工程）断言调用。
// 为什么与主程序 PlcMesBridge 的 IDialogService 平行而不下沉共享：
//   窗集合是程序的私有资产——程序①的窗集（点表配置/工单/权限）与主程序
//   （登录/PLC 配置/调试）完全不同，共用接口会把两个程序的窗耦合死。
// 怎么改：新增窗体在这里加专用方法（生产实现 + Fake 桩 + VM 测试三处同步）。
// =========================================================================

using System.Windows;

namespace CureStation.ViewModels;

public interface IDialogService
{
    /// <summary>提示框（确定）。前阻塞，纯通知。</summary>
    void ShowMessage(string message, string title);

    /// <summary>是否框（是/否）。返回 true=是。</summary>
    bool Confirm(string message, string title);

    /// <summary>打开点表配置窗（模态）。返回 true 表示保存了点表（调用方可重建总线）。</summary>
    bool ShowPointTable();

    /// <summary>打开工艺策略窗（模态）。返回 true 表示保存了策略（调用方可刷新激活策略）。</summary>
    bool ShowCuringPolicy();

    /// <summary>程序化关闭主进程（退出）。</summary>
    void Shutdown();
}

/// <summary>生产实现：真 WPF 窗 + MessageBox。</summary>
public sealed class DialogService : IDialogService
{
    public void ShowMessage(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ShowPointTable()
    {
        // 生产装配：VM 在服务里 new（测试绕过此窗直接实例化 VM 用 Fake 桩）。
        var win = new PointTableWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new PointTableViewModel(new DialogService()),
        };
        return win.ShowDialog() == true;
    }

    public bool ShowCuringPolicy()
    {
        // 生产装配：同点表窗模式（VM 在服务里 new，测试用 Fake 桩）。
        var win = new CuringPolicyWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new CuringPolicyViewModel(new DialogService()),
        };
        return win.ShowDialog() == true;
    }

    public void Shutdown() => Application.Current?.Shutdown();
}
