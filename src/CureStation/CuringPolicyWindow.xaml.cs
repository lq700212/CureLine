// =========================================================================
// CuringPolicyWindow：工艺策略窗（View 装配层——DataContext 由 DialogService 注入）
//
// 可视化画布交互（View 关切，不进 VM）。V0.0.43 起"点格子直接改时长"
// （用户拍板傻瓜式：单击格子→浮层输时长回车即应用）：
//   左键单击=选格（浮层浮出，预定位在选区旁）；左键拖框=框选多格
//   （橡皮筋，Ctrl 叠加）；Ctrl+单击=加选/减选；点空白/Esc=清选择；
//   中键拖动=平移（左键已让给选择，平移第二入口=按住滚轮）；
//   滚轮缩放（以鼠标为锚点，0.4~4.0）；双击空白=复位视图（格子上双击
//   不复位——不打断点选）；右键菜单【适应图像】恢复原始视图。
// 分层：CanvasContent 随变换缩放平移；橡皮筋与点选浮层在 host 坐标系
//   （不随变换，浮层按选区包围盒定位、缩放/平移后重定位）。
// 命中/框选的格子矩形由 ItemContainerGenerator 容器 TransformToVisual
//   求得（视觉矩形含缩放）；选择状态与写值全在 VM（_selectedKeys 真源），
//   View 只做鼠标→格子翻译，与业务零耦合。
// =========================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CureStation.ViewModels;

namespace CureStation;

public partial class CuringPolicyWindow : Window
{
    private const double MinScale = 0.4;
    private const double MaxScale = 4.0;
    private const double WheelStep = 1.15;
    /// <summary>按下后移动超过该像素数（任意轴）即进入框选模式，否则抬起按单击处理。</summary>
    private const double BoxThreshold = 4.0;

    // 中键平移状态（左键已让给点选/框选；记录当前拖动键——不捕获时 MouseMove
    // 出框即丢，标准画布做法）
    private Point _dragOrigin;
    private double _originX;
    private double _originY;
    private MouseButton? _dragButton;

    // 左键点选/框选状态
    private bool _leftDown;
    private bool _boxing;                       // 移动超阈值后进入框选
    private Point _pressPoint;                  // 按下点（CanvasHost 坐标系）
    private Point _boxNow;                      // 框选当前点
    private List<SlotPreviewItem> _boxBase = new();  // Ctrl+框选基线（按下时的选择快照）

    public CuringPolicyWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题条 ✕（无边框自绘标配；保存/激活走命令链——✕ 只关窗不保存）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private CuringPolicyViewModel? Vm => DataContext as CuringPolicyViewModel;

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
        UpdateQuickEditPosition();   // 缩放后选区包围盒变了，浮层跟着走
        e.Handled = true;
    }

    // ---------- 中键平移（按住滚轮拖动；左键保留给点选/框选） ----------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只接管中键；框选按住时（_leftDown）不开始平移——松键顺序交错了会误触发选择。
        // 已在拖动中也忽略新键，避免双键同时按下互相覆盖拖动原点。
        if (e.ChangedButton != MouseButton.Middle || _dragButton != null || _leftDown)
            return;
        _dragButton = e.ChangedButton;
        _dragOrigin = e.GetPosition(CanvasHost);
        _originX = CanvasTranslate.X;
        _originY = CanvasTranslate.Y;
        CanvasHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _dragButton != MouseButton.Middle)
            return;
        _dragButton = null;
        if (CanvasHost.IsMouseCaptured)
            CanvasHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        // 未捕获鼠标时出框即结束拖动（捕获中出框继续拖，回来自动续上）
        if (!CanvasHost.IsMouseCaptured)
            _dragButton = null;
    }

    // ---------- 左键：点选 / 框选（"点格子直接改"主入口） ----------

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragButton != null)
            return;   // 中键平移中不进选择逻辑（防两键交错误选）
        var p = e.GetPosition(CanvasHost);
        // 双击空白=复位视图（格子上双击不复位——不打断点选编辑）
        if (e.ClickCount == 2 && HitItem(p) == null)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        _leftDown = true;
        _boxing = false;
        _pressPoint = p;
        _boxNow = p;
        // Ctrl 按下=框选叠加：先快照当前选择作基线（拖完与框内格子并集）
        _boxBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? SelectedItems()
            : new List<SlotPreviewItem>();
        CanvasHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragButton != null)
        {
            // 中键平移（原路径）
            if (!CanvasHost.IsMouseCaptured)
                return;
            var p = e.GetPosition(CanvasHost);
            CanvasTranslate.X = _originX + (p.X - _dragOrigin.X);
            CanvasTranslate.Y = _originY + (p.Y - _dragOrigin.Y);
            return;
        }
        if (!_leftDown || !CanvasHost.IsMouseCaptured)
            return;
        var q = e.GetPosition(CanvasHost);
        _boxNow = q;
        if (!_boxing && (Math.Abs(q.X - _pressPoint.X) > BoxThreshold ||
                         Math.Abs(q.Y - _pressPoint.Y) > BoxThreshold))
        {
            _boxing = true;
            RubberBand.Visibility = Visibility.Visible;
        }
        if (_boxing)
            UpdateRubberBand();
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_leftDown)
            return;
        _leftDown = false;
        if (CanvasHost.IsMouseCaptured)
            CanvasHost.ReleaseMouseCapture();
        var vm = Vm;
        if (vm != null)
        {
            if (_boxing)
            {
                var hits = ItemsInRect(RubberRect());
                if (_boxBase.Count > 0)
                    vm.AddSelection(_boxBase.Concat(hits).Distinct());
                else
                    vm.ReplaceSelection(hits);   // 空框=清选择（拖框圈空地语义自然）
            }
            else
            {
                var hit = HitItem(_pressPoint);
                if (hit != null)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                        vm.ToggleItem(hit);
                    else
                        vm.ReplaceSelection(new[] { hit });
                }
                else
                {
                    vm.ClearSelection();   // 点空白清选择（浮层随 HasSelection 关闭）
                }
            }
        }
        _boxing = false;
        RubberBand.Visibility = Visibility.Collapsed;
        UpdateQuickEditPosition();
        FocusQuickEditBox();
        e.Handled = true;
    }

    /// <summary>Esc 清除选择（浮层随 HasSelection 关闭）。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Vm?.ClearSelection();
    }

    // ---------- 点选浮层（直接在格子上改时长的输入卡） ----------

    /// <summary>点浮层自己不吃"点空白清选择"（冒泡到画布前拦下）。</summary>
    private void OnQuickEditMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>输入框回车=应用（应用后焦点回输入框，方便连续改）。</summary>
    private void OnQuickEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        var vm = Vm;
        if (vm != null && vm.ApplySelectionCommand.CanExecute(null))
            vm.ApplySelectionCommand.Execute(null);
        FocusQuickEditBox();
        e.Handled = true;
    }

    /// <summary>【应用】按钮：命令在 XAML 绑定执行，这里只把焦点还给输入框。</summary>
    private void OnQuickEditApply(object sender, RoutedEventArgs e) => FocusQuickEditBox();

    /// <summary>浮层显隐变化：显示时定位到选区旁并聚焦输入框（直接打数字）。</summary>
    private void OnQuickEditVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            UpdateQuickEditPosition();
            FocusQuickEditBox();
        }
    }

    /// <summary>浮层定位：贴选区包围盒下方居中，出画布则换上方/钳制在画布内。</summary>
    private void UpdateQuickEditPosition()
    {
        var vm = Vm;
        if (vm == null || !vm.HasSelection || QuickEdit.Visibility != Visibility.Visible)
            return;
        Rect box = Rect.Empty;
        foreach (var it in vm.SlotPreviewItems.Where(x => x.IsSelected))
        {
            var r = GetItemRect(it);
            if (r.IsEmpty)
                continue;
            box = box.IsEmpty ? r : Rect.Union(box, r);
        }
        if (box.IsEmpty)
            return;
        QuickEdit.UpdateLayout();   // 刚从 Collapsed 转 Visible 时 ActualWidth 还没量出来
        double w = QuickEdit.ActualWidth > 0 ? QuickEdit.ActualWidth : QuickEdit.DesiredSize.Width;
        double h = QuickEdit.ActualHeight > 0 ? QuickEdit.ActualHeight : QuickEdit.DesiredSize.Height;
        double hostW = CanvasHost.ActualWidth, hostH = CanvasHost.ActualHeight;
        double x = box.X + box.Width / 2 - w / 2;
        double y = box.Bottom + 8;
        if (y + h > hostH - 4)
            y = box.Top - h - 8;   // 下方放不下→上方
        x = Math.Clamp(x, 4, Math.Max(4, hostW - w - 4));
        y = Math.Clamp(y, 4, Math.Max(4, hostH - h - 4));
        QuickEdit.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>浮层输入框聚焦（直接打数字）。BeginInvoke 到输入队列尾——鼠标 up
    /// 处理器内同步 Focus 会被 WPF 释放捕获后的点击聚焦覆盖（点击落在不可聚焦的
    /// 格子上，焦点被重设回窗），延迟一拍才稳。</summary>
    private void FocusQuickEditBox()
    {
        if (Vm is { HasSelection: true })
            Dispatcher.BeginInvoke(() =>
            {
                QuickEditBox.Focus();
                QuickEditBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- 格子矩形 / 命中测试（host 坐标系，含缩放） ----------

    /// <summary>格子视觉矩形（相对 CanvasHost；宽高乘缩放——RenderSize 是内容坐标）。</summary>
    private Rect GetItemRect(SlotPreviewItem item)
    {
        if (PreviewItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement el)
            return Rect.Empty;
        var topLeft = el.TransformToVisual(CanvasHost).Transform(new Point(0, 0));
        double s = CanvasScale.ScaleX;
        return new Rect(topLeft.X, topLeft.Y, el.ActualWidth * s, el.ActualHeight * s);
    }

    /// <summary>命中测试：点落在哪个格子内（线性扫，格子数 ≤ 架×层，性能无忧）。</summary>
    private SlotPreviewItem? HitItem(Point p)
    {
        var vm = Vm;
        if (vm == null)
            return null;
        foreach (var it in vm.SlotPreviewItems)
        {
            var r = GetItemRect(it);
            if (!r.IsEmpty && r.Contains(p))
                return it;
        }
        return null;
    }

    /// <summary>框选矩形内全部格子（相交即算选中）。</summary>
    private List<SlotPreviewItem> ItemsInRect(Rect rect)
    {
        var list = new List<SlotPreviewItem>();
        var vm = Vm;
        if (vm == null)
            return list;
        foreach (var it in vm.SlotPreviewItems)
        {
            var r = GetItemRect(it);
            if (!r.IsEmpty && r.IntersectsWith(rect))
                list.Add(it);
        }
        return list;
    }

    private List<SlotPreviewItem> SelectedItems() =>
        Vm?.SlotPreviewItems.Where(x => x.IsSelected).ToList() ?? new List<SlotPreviewItem>();

    private Rect RubberRect()
    {
        double x = Math.Min(_pressPoint.X, _boxNow.X);
        double y = Math.Min(_pressPoint.Y, _boxNow.Y);
        return new Rect(x, y, Math.Abs(_pressPoint.X - _boxNow.X), Math.Abs(_pressPoint.Y - _boxNow.Y));
    }

    private void UpdateRubberBand()
    {
        var r = RubberRect();
        Canvas.SetLeft(RubberBand, r.X);
        Canvas.SetTop(RubberBand, r.Y);
        RubberBand.Width = r.Width;
        RubberBand.Height = r.Height;
    }

    // ---------- 视图复位 ----------

    /// <summary>复位视图：缩放回 1、平移回 0（画布内容居中由布局对齐负责）。
    /// 双击空白与右键菜单【适应图像】共用——恢复窗口打开时的原始显示。</summary>
    private void ResetView()
    {
        CanvasScale.ScaleX = 1;
        CanvasScale.ScaleY = 1;
        CanvasTranslate.X = 0;
        CanvasTranslate.Y = 0;
        UpdateQuickEditPosition();
    }

    /// <summary>右键菜单【适应图像】：显示恢复至窗口打开时的原始大小（=复位视图）。</summary>
    private void OnFitImageClick(object sender, RoutedEventArgs e) => ResetView();
}
