// =========================================================================
// StringEqualsConverter：两段字符串相等 → bool（策略库列表"生效中"徽标用）
//
// 干什么：策略库 ListBox 的每一项要对比"本项策略名 == VM.ActiveName（当前
//   生效策略）"来显示绿色【生效中】徽标。WPF 的 DataTrigger/MultiDataTrigger
//   的 Condition 只能填静态值不能绑定（两个绑定没法直接互相比），所以用
//   MultiBinding 把两个字符串一起喂给本转换器返回 bool，再由 DataTrigger
//   按 Value=True 点亮徽标（Ignition 的旧方案：DataTrigger.Binding 里塞
//   MultiBinding——Condition 静态值限制不适用于这种写法）。
// 为什么放转换器：CuringPolicy 是 Core 业务模型不能背 UI 状态（IsActive），
//   给策略包行 VM 又要重写整条列表绑定——对比逻辑收在此处一处就位。
// 怎么改：比较口径 Trim + 忽略大小写（与 VM/Store 的激活名匹配口径完全一致，
//   改一处必须同步另一处）；异常/段数不对一律 false（宁可不显徽标不炸绑定）。
// =========================================================================

using System.Globalization;
using System.Windows.Data;

namespace CureStation;

public sealed class StringEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return false;
        var a = values[0] as string ?? string.Empty;
        var b = values[1] as string ?? string.Empty;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException(); // 纯展示（徽标显隐），不回转
}
