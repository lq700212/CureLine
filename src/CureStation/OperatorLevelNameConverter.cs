// =========================================================================
// OperatorLevelNameConverter：OperatorLevel 枚举 → 中文级别名（权限窗下拉用）
// =========================================================================

using System.Globalization;
using System.Windows.Data;
using CureLine.Core.Security;

namespace CureStation;

public sealed class OperatorLevelNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is OperatorLevel level ? OperatorLevelInfo.DisplayName(level) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException(); // 下拉只出（选中项直接是枚举值），不回转
}
