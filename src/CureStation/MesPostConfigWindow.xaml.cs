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
}
