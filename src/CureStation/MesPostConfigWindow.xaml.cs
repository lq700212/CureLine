using CureStation.ViewModels;
using System.Windows;

namespace CureStation;

/// <summary>
/// 过账配置窗（P3.2 后半）：View 装配层——只设 DataContext（MVVM 纪律，
/// 逻辑全在 MesPostConfigViewModel，测试用 Fake 桩绕过此窗）。
/// </summary>
public partial class MesPostConfigWindow : Window
{
    public MesPostConfigWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题条 ✕（无边框自绘标配；保存走命令行链——✕ 只关窗不保存）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
