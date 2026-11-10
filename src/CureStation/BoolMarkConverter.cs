// =========================================================================
// BoolMarkConverter：布尔 → 中文标记（"是"/空串）
//
// 干什么：追溯报表的"报警"列用文字标记而不是复选框——复选框在窄表格里是
//   个小方块，行内扫一眼读不出"这一件有没有经历过报警"，且勾框占宽度把
//   末列（备注）挤没。本转换器 true→"是"、false→空串（无标记=正常）。
// 怎么改：想换文案改这里一处（报表是唯一消费者）。
// =========================================================================

using System.Globalization;
using System.Windows.Data;

namespace CureStation;

/// <summary>布尔 → 中文标记（true="是"，false=空串）。</summary>
public sealed class BoolMarkConverter : IValueConverter
{
    /// <summary>转换（bool → "是"/空）。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "是" : "";

    /// <summary>反向转换（本窗只读展示，不支持回写）。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
