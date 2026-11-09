// =========================================================================
// SnMaxWidthConverter：SN 行超长等比缩放的「字号下限」约束（V0.0.82）
//
// 干什么：把 SN 行宿主 Grid（SnCardCap）的 ActualWidth 放大 1.35 倍当作
//   里层 TextBlock 的 MaxWidth——11 号字容量不足时 Viewbox 等比缩小
//   （Uniform+DownOnly），但文本宽被 MaxWidth 拦在「卡宽×1.35」，于是缩放
//   因子的下限 ≈ 1/1.35 ≈ 0.74（视觉 ≈ 8 号字）——码再长也不会缩成
//   4~5 号小字（第一版"整串无下限等比缩"被现场实测否决：字太小）。
//   装不下的尾段由 TextTrimming 省略号兜底；超长码的头部与尾部双保留
//   由 VM 的 BindingText 摘要（头 6 + … + 尾 10）负责——17 字符摘要带宽
//   约 128px（SemiBold 雅黑实测），×1.35 才容得下（×1.2 时尾段又被
//   Trimming 砍掉，实测踩过），摘要长度在下限容量以内不再触发省略。
// 怎么改：放大倍数 1.35 = 「允许的最小字号比」的倒数——想最小缩到 0.8
//   倍就把 1.35 写成 1.25（注意别小于 1.3：超长摘要带宽 ≈ 卡宽×1.26，
//   更小的系数会再把摘要尾巴截掉）；想锁死原字号（不缩）就写成 1.0
//   并同步放宽 VM 摘要长度。只影响 SN 行视觉，零业务牵连。
// 边界：ActualWidth 在首次布局前 = 0——MaxWidth=0 会把文本压成空帧，
//   此处按 ~两倍窄屏卡宽（240）兜底，首帧短暂偏宽随后布局接管回归正常
//   （布局一拍完成，肉眼不可感）。
// =========================================================================

using System.Globalization;
using System.Windows.Data;

namespace CureStation;

public sealed class SnMaxWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 宿主实际宽度；未布局（0）或异常时按窄屏卡宽两倍兜底，防首帧文本被压空
        var hostWidth = value is double w && w > 0 ? w : 240d;
        return hostWidth * 1.35; // 1.35 = 缩放下限（≈0.74 倍 ≈ 8 号字）的倒数；<1.3 会截掉摘要尾巴（实测）
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException(); // 纯展示只出（OneWay 绑定），不回转
}
