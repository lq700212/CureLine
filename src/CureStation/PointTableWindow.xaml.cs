// =========================================================================
// PointTableWindow：IO 点表配置窗（View 装配层，DataContext 由 DialogService 注入）
// =========================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CureStation;

public partial class PointTableWindow : Window
{
    public PointTableWindow()
    {
        InitializeComponent();
    }

    /// <summary>标题条 ✕（无边框自绘标配）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 绑定表"单击即编辑"（V0.0.46 补傻瓜式手感的最后一环）：
    /// DataGrid 缺省交互=单击选中、双击/二次单击才进入编辑——现场单击像
    /// "没反应"。文本列（块/架/层/功能名）在这里改成**单击一次直接进编辑**：
    /// 自己全权负责（e.Handled 阻断缺省链——缺省要到二次单击才 BeginEdit，
    /// 且它自己的 current cell 切换会把刚开的编辑提交掉），设当前格+选行+
    /// BeginEdit；已在编辑的格子放行（保留文本框内部点击定位光标）；表头/
    /// 滚动条走缺省。方向/角色下拉列不拦：常驻 ComboBox
    /// 自己接住点击直接展开下拉（V0.0.46 由 DataGridComboBoxColumn 编辑态
    /// 模板改为 TemplateColumn 常驻，见 XAML 注释）。
    /// </summary>
    private void OnBindingsGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 命中下拉列（常驻 ComboBox）：交给控件自己，第一次点击就是展开下拉
        if (TryFindAncestor<ComboBox>(e.OriginalSource as DependencyObject) != null)
            return;
        var cell = TryFindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        // 表头/滚动条/空白区：走 DataGrid 缺省（列头排序、滚动条拖动等）
        if (cell == null || cell.IsEditing || cell.IsReadOnly)
            return;
        // 只拦文本列的展示态格子（display 模板 Content=TextBlock；
        // 下拉列格子 Content=ComboBox 已在上一行排除）
        if (cell.Content is not TextBlock)
            return;
        var grid = (DataGrid)sender;
        e.Handled = true;
        grid.UnselectAll();
        grid.SelectedItem = cell.DataContext;   // 选中行（"删除选中行"按钮随之可用）
        grid.CurrentCell = new DataGridCellInfo(cell);
        grid.Focus();
        grid.BeginEdit();   // 基于 CurrentCell 进编辑（先设 CurrentCell 再调）
    }

    /// <summary>沿可视树向上找指定类型控件（找不到返回 null）。</summary>
    private static T? TryFindAncestor<T>(DependencyObject? start) where T : class
    {
        while (start is not null)
        {
            if (start is T hit)
                return hit;
            start = VisualTreeHelper.GetParent(start);
        }
        return null;
    }
}
