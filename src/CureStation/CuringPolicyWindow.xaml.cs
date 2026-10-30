// =========================================================================
// CuringPolicyWindow：工艺策略窗（View 装配层——DataContext 由 DialogService 注入）
//
// 可视化画布交互（View 关切，不进 VM）：滚轮缩放（以鼠标为锚点，0.4~4.0）、
// 左键/中键拖动平移（鼠标捕获；中键=按住滚轮）、双击复位、右键菜单
// 【适应图像】恢复窗口打开时的原始视图。格子数据与分档色在 VM（SlotPreviewItems），
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

    // 拖动状态（鼠标捕获期间有效；记录当前拖动键——左键与中键各自按下/抬起，
    // 互不混：中键拖动中途松左键不打断，反之亦然。不捕获时 MouseMove 出框即丢——
    // 标准画布做法）
    private Point _dragOrigin;
    private double _originX;
    private double _originY;
    private MouseButton? _dragButton;

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

    // ---------- 拖动平移（左键 / 中键=按住滚轮，共用一条平移路径） ----------

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击复位（ClickCount==2；单/双击都从 Down 走，双击时顺带复位并结束拖动）
        if (e.ClickCount == 2)
        {
            ResetView();
            _dragButton = null;
            CanvasHost.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        StartDrag(e);
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 中键（按住滚轮）拖动=第二平移入口（左键保留；滚轮滚动已占缩放）。
        // 只接管中键，其它键不碰——免得与各键专用事件（MouseLeftButtonDown 族）打架；
        // 已在拖动中也忽略新键，避免双键同时按下互相覆盖拖动原点。
        if (e.ChangedButton != MouseButton.Middle || _dragButton != null)
            return;
        StartDrag(e);
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragButton == null || !CanvasHost.IsMouseCaptured)
            return;
        var p = e.GetPosition(CanvasHost);
        CanvasTranslate.X = _originX + (p.X - _dragOrigin.X);
        CanvasTranslate.Y = _originY + (p.Y - _dragOrigin.Y);
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag(e);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        EndDrag(e);
        e.Handled = true;
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        // 未捕获鼠标时出框即结束拖动（捕获中出框继续拖，回来自动续上）
        if (!CanvasHost.IsMouseCaptured)
            _dragButton = null;
    }

    /// <summary>起拖：记按键/原点/当前平移量并捕获鼠标（左键与中键共用）。</summary>
    private void StartDrag(MouseButtonEventArgs e)
    {
        _dragButton = e.ChangedButton;
        _dragOrigin = e.GetPosition(CanvasHost);
        _originX = CanvasTranslate.X;
        _originY = CanvasTranslate.Y;
        CanvasHost.CaptureMouse();
    }

    /// <summary>落拖：只有放下"当前正拖的那个键"才结束（拖动中途松另一个键不打断）。</summary>
    private void EndDrag(MouseButtonEventArgs e)
    {
        if (_dragButton != e.ChangedButton)
            return;
        _dragButton = null;
        if (CanvasHost.IsMouseCaptured)
            CanvasHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>复位视图：缩放回 1、平移回 0（画布内容居中由布局对齐负责）。
    /// 双击与右键菜单【适应图像】共用——恢复窗口打开时的原始显示。</summary>
    private void ResetView()
    {
        CanvasScale.ScaleX = 1;
        CanvasScale.ScaleY = 1;
        CanvasTranslate.X = 0;
        CanvasTranslate.Y = 0;
    }

    /// <summary>右键菜单【适应图像】：显示恢复至窗口打开时的原始大小（=复位视图）。</summary>
    private void OnFitImageClick(object sender, RoutedEventArgs e) => ResetView();
}
