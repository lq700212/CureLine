// =========================================================================
// WoQtySplitConverter：工单数量 "37/120" → 拆产出数（"37"）/ 总量（"120"）
//
// 干什么：工单双卡 sub 行"已产出 37 / 120"（HTML g2-wo-sub 直译：产出数
//   大字上色 R=蓝/L=紫，剩余灰小字）需要把 WorkOrderQtyR/L 的组合字符串
//   拆开分色展示——VM 零改动（该格式被 SummarizeWorkOrder 测试断言锁死，
//   拆分放在展示层转换器是唯一不动业务口径的做法）。
// 怎么改：ConverterParameter="0" 取'/'前段（已产出）、="1" 取后段（总量）；
//   不是"A/B"两段形状（如 "—"、空）时原样返回（原串直接显示不炸）。
// =========================================================================

using System.Globalization;
using System.Windows.Data;

namespace CureStation;

public sealed class WoQtySplitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        var takeSecond = string.Equals(parameter as string, "1", StringComparison.Ordinal);
        var parts = text.Split('/');
        return parts.Length == 2 ? parts[takeSecond ? 1 : 0].Trim() : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException(); // 展示只出（进度/产出都是 OneWay 绑定），不回转
}
