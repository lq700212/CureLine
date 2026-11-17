using CureStation.ViewModels;
using Kaleidoscope.UI.Views;

namespace CureStation;

/// <summary>
/// 过账配置窗（P3.2 后半）：View 装配层——只设 DataContext（MVVM 纪律，
/// 逻辑全在 MesPostConfigViewModel，测试用 Fake 桩绕过此窗）。
/// 圆角窗骨架（V0.0.109 沉库）：继承 ChromeDialogWindow——透明分层窗/
/// WindowChrome/圆角 Border/投影/标题条/✕ 全在基类，本窗只留内容区。
/// </summary>
public partial class MesPostConfigWindow : ChromeDialogWindow
{
    public MesPostConfigWindow()
    {
        InitializeComponent();
    }
}
