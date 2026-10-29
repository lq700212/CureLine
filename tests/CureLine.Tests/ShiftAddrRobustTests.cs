// =========================================================================
// 分段地址算法测试：ShiftAddr 合法 heritage + 非法兜底（走查 B3）
//
// 合法输入与老项目逐字一致（无补零：R17900+30=R17930）；
// 非法输入（配错地址）返回原地址、不抛——调用方走 WriteFail 回写，
// 不炸后台线程。大小写不敏感（"r17900" 也认 R）。
// =========================================================================

using CureLine.Core.Modes;

namespace CureLine.Tests;

public class ShiftAddrRobustTests
{
    [Fact(DisplayName = "合法地址无补零")]
    public void ShiftAddr_Good_NoPadding()
    {
        Assert.Equal("R17930", StationGateway.ShiftAddr("R17900", 30));
        Assert.Equal("R17960", StationGateway.ShiftAddr("R17900", 60));
        Assert.Equal("D2910", StationGateway.ShiftAddr("D2880", 30));
    }

    [Fact(DisplayName = "小写前缀同样识别")]
    public void ShiftAddr_LowerCase_Prefix()
    {
        Assert.Equal("R17930", StationGateway.ShiftAddr("r17900", 30));
        Assert.Equal("D2910", StationGateway.ShiftAddr("d2880", 30));
    }

    [Fact(DisplayName = "非法地址返回原值不抛")]
    public void ShiftAddr_Bad_ReturnsOriginal_NoThrow()
    {
        foreach (string bad in new[] { "", "R", "D", "RABC", "X17900", "R17900A" })
        {
            string got = StationGateway.ShiftAddr(bad, 30);
            Assert.Equal(bad, got);
        }
    }

    [Fact(DisplayName = "偏移0返回等值地址")]
    public void ShiftAddr_ZeroOffset_SameNumber()
    {
        Assert.Equal("R17900", StationGateway.ShiftAddr("R17900", 0));
    }
}
