// =========================================================================
// PointTableTests：点表模型/存储/缺省模板/校验（程序①的通讯地图质量闸）
// 地址布局断言直接对照 Audi cover module IO 表（2026-09-21）：块1 DI@0x1000
// DO@0x1010、块 7 DI@0x10C0 DO@0x10D0、块间步进 0x20。
// =========================================================================

using PlcMesBridge.Core.Comms;
using PlcMesBridge.Tests.Mocks;
using Xunit;

namespace PlcMesBridge.Tests;

public class PointTableTests
{
    private static IoPointBinding Find(IoCouplerDevice d, bool isInput,
        IoPointRole role, int rack, int level) =>
        d.Bindings.Single(b => b.IsInput == isInput && b.Role == role &&
            b.Rack == rack && b.Level == level);

    // ---------- 缺省模板 ----------

    [Fact]
    public void DefaultTemplate_TenRacksThreeLevels_Has180ConfiguredPoints()
    {
        var config = IoPointTableStore.BuildDefaultTemplate("T", "192.168.1.2", 10, 3);
        var dev = Assert.Single(config.Devices);
        Assert.Equal(10, dev.BlockCount);
        // 30 仓位 × (3 入 + 3 出) = 180 个业务点；预留位不生成
        Assert.Equal(180, dev.Bindings.Count);
        Assert.Equal(90, dev.Bindings.Count(b => b.IsInput));
        Assert.Equal(90, dev.Bindings.Count(b => !b.IsInput));
        Assert.True(PointTableValidator.Validate(config).Ok);
    }

    [Fact]
    public void DefaultTemplate_BlockLayoutMatchesIoTable()
    {
        var config = IoPointTableStore.BuildDefaultTemplate("T", "192.168.1.2", 10, 3);
        var dev = config.Devices[0];
        // IO 表：块1 DI@0x1000/DO@0x1010，块 7 DI@0x10C0/DO@0x10D0，步进 0x20
        Assert.Equal(0x1000, dev.DiAddressOf(1));
        Assert.Equal(0x1010, dev.DoAddressOf(1));
        Assert.Equal(0x10C0, dev.DiAddressOf(7));
        Assert.Equal(0x10D0, dev.DoAddressOf(7));
        Assert.Equal(0x20, dev.BlockStrideRegisters);
    }

    [Fact]
    public void DefaultTemplate_InBlockBitLayout_PerLevelGroupOf4()
    {
        var config = IoPointTableStore.BuildDefaultTemplate("T", "192.168.1.2", 10, 3);
        var dev = config.Devices[0];
        // 块 5（=架 5）：层 1 bit0~2、层 2 bit4~6、层 3 bit8~10（组内 0=L光栅 1=R光栅 2=托盘 / 输出 0红1黄2绿）
        Assert.Equal(0, Find(dev, true, IoPointRole.RackLightL, 5, 1).Bit);
        Assert.Equal(1, Find(dev, true, IoPointRole.RackLightR, 5, 1).Bit);
        Assert.Equal(2, Find(dev, true, IoPointRole.TrayDetect, 5, 1).Bit);
        Assert.Equal(4, Find(dev, true, IoPointRole.RackLightL, 5, 2).Bit);
        Assert.Equal(10, Find(dev, true, IoPointRole.TrayDetect, 5, 3).Bit);
        Assert.Equal(0, Find(dev, false, IoPointRole.LampRed, 5, 1).Bit);
        Assert.Equal(1, Find(dev, false, IoPointRole.LampYellow, 5, 1).Bit);
        Assert.Equal(6, Find(dev, false, IoPointRole.LampGreen, 5, 2).Bit);
        // 块 9、10 跟上（不越界）
        Assert.Equal(10, Find(dev, true, IoPointRole.TrayDetect, 9, 3).Bit);
        Assert.Equal(8, Find(dev, true, IoPointRole.RackLightL, 10, 3).Bit);
    }

    // ---------- 持久化（含自愈） ----------

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var scope = new TestScope();
        var config = IoPointTableStore.BuildDefaultTemplate("A线", "192.168.1.30", 10, 3);
        config.Devices[0].Port = 1502;
        IoPointTableStore.Save(config);

        var loaded = IoPointTableStore.Load();
        Assert.NotNull(loaded);
        var dev = Assert.Single(loaded!.Devices);
        Assert.Equal("A线", dev.Name);
        Assert.Equal("192.168.1.30", dev.Ip);
        Assert.Equal(1502, dev.Port);
        Assert.Equal(180, dev.Bindings.Count);
        Assert.Equal(IoPointRole.TrayDetect,
            Find(dev, true, IoPointRole.TrayDetect, 3, 2).Role);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        var reasons = new List<string>();
        Assert.Null(IoPointTableStore.Load(reasons: reasons));
        Assert.Contains(reasons, r => r.Contains("不存在"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPathsDir());
        File.WriteAllText(IoPointTableStore.DefaultPath, "{ 不是 json ");
        var reasons = new List<string>();
        Assert.Null(IoPointTableStore.Load(reasons: reasons));
        Assert.Contains(reasons, r => r.Contains("损坏"));
    }

    private static string AppPathsDir() =>
        System.IO.Path.GetDirectoryName(IoPointTableStore.DefaultPath)!;

    // ---------- 校验（保存前拦截） ----------

    [Fact]
    public void Validation_Catches_DuplicatePhysicalBinding()
    {
        var config = new PointTableConfig
        {
            Devices =
            {
                new IoCouplerDevice { Bindings =
                {
                    new IoPointBinding { Block = 1, Bit = 0, IsInput = true,
                        Role = IoPointRole.RackLightL, Rack = 1, Level = 1 },
                    // 同一物理点重复绑第二行
                    new IoPointBinding { Block = 1, Bit = 0, IsInput = true,
                        Role = IoPointRole.RackLightL, Rack = 2, Level = 1 },
                } },
            },
        };
        var v = PointTableValidator.Validate(config);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("重复绑定"));
    }

    [Fact]
    public void Validation_Catches_SemanticDuplicate()
    {
        var config = new PointTableConfig
        {
            Devices =
            {
                new IoCouplerDevice { Bindings =
                {
                    new IoPointBinding { Block = 1, Bit = 0, IsInput = true,
                        Role = IoPointRole.TrayDetect, Rack = 1, Level = 1 },
                    // 同一语义键绑第二个物理点（状态机会拿到二义值）
                    new IoPointBinding { Block = 2, Bit = 3, IsInput = true,
                        Role = IoPointRole.TrayDetect, Rack = 1, Level = 1 },
                } },
            },
        };
        var v = PointTableValidator.Validate(config);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("多个物理点"));
    }

    [Fact]
    public void Validation_Catches_BadIpAndDirection()
    {
        var config = new PointTableConfig
        {
            Devices =
            {
                new IoCouplerDevice
                {
                    Ip = "not-an-ip",
                    PollIntervalMs = 10,
                    Bindings =
                    {
                        // 灯是输出角色，却绑在输入点上 → 方向错误
                        new IoPointBinding { Block = 1, Bit = 0, IsInput = true,
                            Role = IoPointRole.LampRed, Rack = 1, Level = 1 },
                    },
                },
            },
        };
        var v = PointTableValidator.Validate(config);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("IP 非法"));
        Assert.Contains(v.Errors, e => e.Contains("轮询周期"));
        Assert.Contains(v.Errors, e => e.Contains("角色与点方向不符"));
    }

    [Fact]
    public void Validation_ReservedMustHaveZeroSlot()
    {
        var config = new PointTableConfig
        {
            Devices =
            {
                new IoCouplerDevice { Bindings =
                {
                    new IoPointBinding { Block = 1, Bit = 15,
                        Role = IoPointRole.Reserved, Rack = 1, Level = 1 },
                } },
            },
        };
        var v = PointTableValidator.Validate(config);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("仓位必须清零"));
    }
}
