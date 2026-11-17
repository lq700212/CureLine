using CimGateway.ViewModels;
using CureLine.ViewModels;
using Kaleidoscope.UI.Views;

namespace CimGateway;

/// <summary>
/// 调试窗（MVVM 的 View：只设 DataContext）。
/// 表单/预览/上传全在 MesDebugViewModel。
/// 圆角窗骨架（V0.0.109 沉库）：继承 ChromeDialogWindow（可缩放变体
/// Resizeable=True）——标题条/✕ 在基类，本窗只留内容区装配。
/// </summary>
public partial class MesDebugWindow : ChromeDialogWindow
{
    public MesDebugViewModel ViewModel { get; }

    public MesDebugWindow()
    {
        ViewModel = new MesDebugViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }
}
