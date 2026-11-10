// =========================================================================
// InverseBoolConverter：bool 取反（扫码枪配置窗"连接方式"单选用）
//
// 干什么：TCP/串口两只 RadioButton 同组互斥——TCP 钮双向绑定 EditUseTcp，
//   串口钮只做显示（OneWay + 本转换器取反）。不能给两只 Radio 都挂双向
//   取反绑定：点击串口时 WPF 先自动取消 TCP 钮（其 ConvertBack(false) 取反
//   写回 true）再走串口自己的 ConvertBack(true) 写回 false，两次回写顺序
//   一颠倒 VM 就收到错值——所以只让一只钮双向，另一只纯显示，回写链唯一。
// 怎么改：只做 bool 取反；null/非 bool 一律回 false（绑定失败安全侧）。
// =========================================================================

using System.Globalization;
using System.Windows.Data;

namespace CureStation;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}
