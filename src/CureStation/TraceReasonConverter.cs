// =========================================================================
// TraceReasonConverter：追溯结束原因枚举 → 中文文案（XAML 绑定用）
//
// 干什么：追溯流水行的"结束原因"在 Core 里是枚举（SlotTraceEndReason），
//   界面上要显示中文（正常放行/中途取出/…）。本转换器把枚举翻成
//   SlotTraceEndReasonText 的统一文案——与日志、导出 CSV 同一出处，
//   不在 XAML 里另拼字符串（改文案只改一处）。
// 怎么改：文案改 SlotTraceEndReasonText.ToText；本转换器不用动。
// =========================================================================

using System.Globalization;
using System.Windows.Data;
using CureLine.Core.Data;

namespace CureStation;

/// <summary>枚举 → 中文文案（单向；null/未知返回空串）。</summary>
public sealed class TraceReasonConverter : IValueConverter
{
    /// <summary>转换（枚举 → 中文）。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SlotTraceEndReason reason)
            return SlotTraceEndReasonText.ToText(reason);
        return "";
    }

    /// <summary>反向转换（本窗只读展示，不支持回写）。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
