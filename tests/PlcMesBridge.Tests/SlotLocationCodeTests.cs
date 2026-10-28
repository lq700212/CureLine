// =========================================================================
// SlotLocationCodeTests：客户库位编码（Date Matrix）换算用例
// 编码表出处：客户《自然静态养护(24H)库位编码表》——10 站一排（A~J）× 3 层，
// 数字 = 列序 + 层序 - 1。改编码规则先改 SlotLocationCode 头注释再改断言。
// =========================================================================

using PlcMesBridge.Core.Curing;
using Xunit;

namespace PlcMesBridge.Tests;

public class SlotLocationCodeTests
{
    // ---------- Encode：逐格对照客户编码表（三排抽样覆盖四角+中列） ----------

    [Theory]
    [InlineData(1, 1, "A01")]   // 第一排左起第 1 站（上层）
    [InlineData(2, 1, "B02")]   // 第一排左起第 2 站
    [InlineData(5, 1, "E05")]   // 第一排中列
    [InlineData(10, 1, "J10")]  // 第一排末站
    [InlineData(1, 2, "A02")]   // 第二排（中层）
    [InlineData(2, 2, "B03")]
    [InlineData(10, 2, "J11")]
    [InlineData(1, 3, "A03")]   // 第三排（下层）
    [InlineData(2, 3, "B04")]
    [InlineData(10, 3, "J12")]  // 第三排末站
    public void Encode_MatchesCustomerTable(int rack, int level, string expected)
    {
        Assert.Equal(expected, SlotLocationCode.Encode(rack, level));
    }

    [Fact]
    public void Encode_All30Slots_UniqueCodes()
    {
        // 全表 30 格编码互不重复——扫码绑定的前提（编码必须能唯一定位仓位）
        var codes = new HashSet<string>();
        for (int rack = 1; rack <= SlotLocationCode.RacksPerRow; rack++)
        for (int level = 1; level <= SlotLocationCode.Levels; level++)
            codes.Add(SlotLocationCode.Encode(rack, level));

        Assert.Equal(30, codes.Count);
    }

    [Fact]
    public void Encode_OutOfRange_ReturnsPlaceholder()
    {
        // 越界（点表绑了 >10 架 / >3 层）给占位码，不抛异常不猜码
        Assert.Equal("R11L01", SlotLocationCode.Encode(11, 1));
        Assert.Equal("R01L04", SlotLocationCode.Encode(1, 4));
        Assert.Equal("R00L01", SlotLocationCode.Encode(0, 1));
    }

    // ---------- TryDecode：扫码绑定预留入口 ----------

    [Fact]
    public void TryDecode_RoundTrip_All30Slots()
    {
        // Encode → TryDecode 往返还原（界面显示与扫码绑定同源互逆）
        for (int rack = 1; rack <= SlotLocationCode.RacksPerRow; rack++)
        for (int level = 1; level <= SlotLocationCode.Levels; level++)
        {
            var code = SlotLocationCode.Encode(rack, level);
            Assert.True(SlotLocationCode.TryDecode(code, out var r, out var l),
                $"编码 {code} 应可反解");
            Assert.Equal(rack, r);
            Assert.Equal(level, l);
        }
    }

    [Theory]
    [InlineData("a01", 1, 1)]   // 小写兼容（扫码枪可能出小写）
    [InlineData("J12", 10, 3)]
    public void TryDecode_ValidCodes_Parses(string code, int rack, int level)
    {
        Assert.True(SlotLocationCode.TryDecode(code, out var r, out var l));
        Assert.Equal(rack, r);
        Assert.Equal(level, l);
    }

    [Theory]
    [InlineData(null)]      // 空引用
    [InlineData("")]        // 空串
    [InlineData("A1")]      // 缺位（表内口径必须两位数字）
    [InlineData("A001")]    // 超长
    [InlineData("K01")]     // 列越界（无 K 列）
    [InlineData("A00")]     // 数字 0 → 层 0
    [InlineData("A04")]     // A 列最大 A03，04 反解层 4 越界
    [InlineData("J13")]     // J 列最大 J12
    [InlineData("A0X")]    // 非数字
    public void TryDecode_InvalidCodes_Rejected(string? code)
    {
        Assert.False(SlotLocationCode.TryDecode(code, out _, out _));
    }
}
