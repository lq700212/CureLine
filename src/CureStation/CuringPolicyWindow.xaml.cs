// =========================================================================
// CuringPolicyWindow：工艺策略窗（View 装配层——DataContext 由 DialogService 注入）
//
// 可视化画布交互（View 关切，不进 VM）：滚轮缩放（以鼠标为锚点，0.4~4.0）、
// 左键拖动平移（鼠标捕获）、双击复位。格子数据与分档色在 VM（SlotPreviewItems），
// 这里的变换矩阵只动 CanvasContent 的 RenderTransform，与业务零耦合。
// =========================================================================

using System.Windows;
using System.Windows.Input;
using CureStation.ViewModels;

namespace CureStation;

public partial class CuringPolicyWindow : Window
{
    private const double MinScale = 0.4;
    private const double MaxScale = 4.0;
    private const double WheelStep = 1.15;

    // 拖动状态（鼠标捕获期间有效；不捕获时 MouseMove 出框即丢——标准画布做法）
    private Point _dragOrigin;
    private double _originX;
    private double _originY;
    private bool _dragging;

    public CuringPolicyWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题条 ✕（无边框自绘标配；保存/激活走命令链——✕ 只关窗不保存）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- 滚轮缩放（以鼠标位置为锚点：缩放前后鼠标下的内容点不动） ----------

    private void OnCanvasPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? WheelStep : 1 / WheelStep;
        double newScale = Math.Clamp(CanvasScale.ScaleX * factor, MinScale, MaxScale);
        factor = newScale / CanvasScale.ScaleX;   // 钳制后实际生效的倍率（到边界不再变）
        if (Math.Abs(factor - 1) < 0.0001)
        {
            e.Handled = true;
            return;
        }
        var p = e.GetPosition(CanvasHost);
        CanvasTranslate.X = p.X - (p.X - CanvasTranslate.X) * factor;
        CanvasTranslate.Y = p.Y - (p.Y - CanvasTranslate.Y) * factor;
        CanvasScale.ScaleX = newScale;
        CanvasScale.ScaleY = newScale;
        e.Handled = true;
    }

    // ---------- 左键拖动平移 ----------

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击复位（ClickCount==2；单/双击都从 Down 走，双击时顺带复位并结束拖动）
        if (e.ClickCount == 2)
        {
            ResetView();
            _dragging = false;
            CanvasHost.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragOrigin = e.GetPosition(CanvasHost);
        _originX = CanvasTranslate.X;
        _originY = CanvasTranslate.Y;
        CanvasHost.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !CanvasHost.IsMouseCaptured)
            return;
        var p = e.GetPosition(CanvasHost);
        CanvasTranslate.X = _originX + (p.X - _dragOrigin.X);
        CanvasTranslate.Y = _originY + (p.Y - _dragOrigin.Y);
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        if (CanvasHost.IsMouseCaptured)
            CanvasHost.ReleaseMouseCapture();
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        // 未捕获鼠标时出框即结束拖动（捕获中出框继续拖，回来自动续上）
        if (!CanvasHost.IsMouseCaptured)
            _dragging = false;
    }

    /// <summary>复位视图：缩放回 1、平移回 0（画布内容居中由布局对齐负责）。</summary>
    private void ResetView()
    {
        CanvasScale.ScaleX = 1;
        CanvasScale.ScaleY = 1;
        CanvasTranslate.X = 0;
        CanvasTranslate.Y = 0;
    }
}
