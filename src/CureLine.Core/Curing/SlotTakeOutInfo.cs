// =========================================================================
// SlotTakeOutInfo：产品的"中途取出"履历（再加工放回时的判据）
//
// 干什么：记"这个产品被中途取出过、是从哪个库位取出的、取出时累计已固化多久"
//   ——这是"再加工后放回来"这条业务链的唯一判据来源：
//     ① 放回原库位 → 拦截（同一库位产品的固化时间必须统一，中途取出的产品
//        放回原库位会与同库位其他产品的固化起点/时长对不上）；
//     ② 放到新库位 → 弹窗让操作员确认固化时长口径（接着之前固化继续 /
//        完全重新开始固化），"之前已固化多久"就是本履历里的分钟数。
//
// 数据来源：固化追溯库（SQLite 真源，slot_product_trace 里 end_reason=中途取出
//   的行）。扫描在 UI 线程做（铁律 2：UI 线程不做 DB IO），所以宿主在启动
//   时后台查一次装进内存索引，之后随事件增量维护——本类只描述"事实长什么样"。
// =========================================================================

namespace CureLine.Core.Curing;

/// <summary>一个产品的"中途取出"履历（SN → 取出库位 + 累计已固化分钟）。</summary>
public sealed class SlotTakeOutInfo
{
    /// <summary>产品 SN（二维码原文）。</summary>
    public string ProductSn { get; set; } = "";

    /// <summary>取出时的全局架号（点表口径 1~N，双架时第二台从 11 起）。</summary>
    public int Rack { get; set; }

    /// <summary>取出时的层号（1~3）。</summary>
    public int Level { get; set; }

    /// <summary>
    /// 取出时该产品**累计**已固化分钟（含更早几轮续接的累计——续接件再被取出
    /// 时累计要接着算，否则"接着之前固化继续"的基准会算少）。
    /// </summary>
    public int CuredMinutes { get; set; }

    /// <summary>取出时刻（墙钟）。</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>取出时的库位显示码（A01~J12，与主窗卡片标题同源）。</summary>
    public string LocationCode => SlotLocationCode.EncodeInRack(Rack, Level);
}
