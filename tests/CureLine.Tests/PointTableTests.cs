// =========================================================================
// PointTableTests：点表模型/存储/缺省模板/校验（程序①的通讯地图质量闸）
// 地址布局断言直接对照 Audi cover module IO 表（2026-09-21）：块1 DI@0x1000
// DO@0x1010、块 7 DI@0x10C0 DO@0x10D0、块间步进 0x20。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Tests.Mocks;
using Xunit;

namespace CureLine.Tests;

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

    [Fact]
    public void DefaultTemplate_RackStart11_RacksShifted_AddressUnchanged()
    {
        // 双架第二台（V0.0.20）：rackStart=11 → 绑定架号 11~20；
        // 设备内地址换算不受影响——块号仍从 1 起（块1=DiStartAddress）。
        var config = IoPointTableStore.BuildDefaultTemplate("2号架", "192.168.1.21", 10, 3, 11);
        var dev = Assert.Single(config.Devices);
        Assert.Equal(11, dev.Bindings.Min(b => b.Rack));
        Assert.Equal(20, dev.Bindings.Max(b => b.Rack));
        Assert.Equal(180, dev.Bindings.Count);
        // 地址锚点：块 1/2 的 DI 地址与单架模板完全一致（架号只是业务语义键）
        Assert.Equal(0x1000, dev.DiAddressOf(1));
        Assert.Equal(0x1020, dev.DiAddressOf(2));
        Assert.Equal(0x1010, dev.DoAddressOf(1));
        Assert.True(PointTableValidator.Validate(config).Ok);
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
    public void Validation_Catches_SemanticDuplicate_AcrossDevices()
    {
        // 跨设备语义重复（V0.0.20 双架防线）：两台耦合器都绑架 1 层 1——
        // 快照把多台设备按语义键合并进同一字典，撞号会同键互相覆盖；
        // 双架第二台必须绑独立架号段（如 11~20），保存前在此拦下。
        var d1 = IoPointTableStore.BuildDefaultTemplate("1号架", "127.0.0.1", 10, 3, 1);
        var d2 = IoPointTableStore.BuildDefaultTemplate("2号架", "127.0.0.2", 10, 3, 1); // 撞架号
        var v = PointTableValidator.Validate(new PointTableConfig
        {
            Devices = { d1.Devices[0], d2.Devices[0] },
        });
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("架号必须全局唯一"));
    }

    [Fact]
    public void Validation_TwoDevicesWithDistinctRacks_Pass()
    {
        // 双架正例：第二台绑架 11~20，全场语义唯一 → 校验通过
        var d1 = IoPointTableStore.BuildDefaultTemplate("1号架", "127.0.0.1", 10, 3, 1);
        var d2 = IoPointTableStore.BuildDefaultTemplate("2号架", "127.0.0.2", 10, 3, 11);
        var v = PointTableValidator.Validate(new PointTableConfig
        {
            Devices = { d1.Devices[0], d2.Devices[0] },
        });
        Assert.True(v.Ok, string.Join("\r\n", v.Errors));
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

    // ---------- 机台批量模板（V0.0.41：10 台 GFNB-1A3A 现场口径） ----------

    [Fact]
    public void MachineTemplate_Defaults_MatchSiteLayout()
    {
        // 缺省参数=现场现值：10 台、IP 192.168.1.20~.29、每台 1 架 3 层
        var config = IoPointTableStore.BuildMachineTemplate();
        Assert.Equal(10, config.Devices.Count);
        for (int i = 0; i < 10; i++)
        {
            var d = config.Devices[i];
            Assert.Equal($"IO耦合器{i + 1}", d.Name);
            Assert.Equal($"192.168.1.{20 + i}", d.Ip);
            Assert.Equal(502, d.Port);
            Assert.Equal(1, d.BlockCount);            // GFNB-1A3A = 16DI+16DO 一块
            Assert.Equal(0x1000, d.DiStartAddress);   // iO-GRID 家族占位，现场实测后配置窗改
            Assert.Equal(0x2000, d.DoStartAddress);
            Assert.Equal(18, d.Bindings.Count);       // 1 架 3 层 × (3 入 + 3 出)
            Assert.Equal(i + 1, d.Bindings[0].Rack);  // 架号=列号连续排（跨设备不撞）
        }
        Assert.True(PointTableValidator.Validate(config).Ok);
    }

    [Fact]
    public void MachineTemplate_CustomCountIpStepRackStart()
    {
        // 3 台、首址 10.0.0.5 步长 2、起始架号 11（第二台机台扩线场景）
        var config = IoPointTableStore.BuildMachineTemplate(
            deviceCount: 3, rackStart: 11, levelsPerRack: 3, ipFirst: "10.0.0.5", ipStep: 2);
        Assert.Equal(3, config.Devices.Count);
        Assert.Equal("10.0.0.5", config.Devices[0].Ip);
        Assert.Equal("10.0.0.7", config.Devices[1].Ip);
        Assert.Equal("10.0.0.9", config.Devices[2].Ip);
        Assert.Equal(11, config.Devices[0].Bindings[0].Rack);
        Assert.Equal(13, config.Devices[2].Bindings[0].Rack);
        Assert.True(PointTableValidator.Validate(config).Ok);
    }

    [Fact]
    public void MachineTemplate_ZeroStepWithMultipleDevices_Throws()
    {
        // 多台步长 0 = 全部同 IP（现场连不上）——生成器当场拦
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IoPointTableStore.BuildMachineTemplate(deviceCount: 5, ipStep: 0));
    }

    // ---------- 校验器多台闸（V0.0.41：重复 IP / 重名） ----------

    [Fact]
    public void Validation_DuplicateIpPort_Rejected()
    {
        var config = new PointTableConfig
        {
            Devices = { MakeBareDevice("A", "192.168.1.20"), MakeBareDevice("B", "192.168.1.20") },
        };
        var v = PointTableValidator.Validate(config);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("重复") && e.Contains("192.168.1.20:502"));
    }

    [Fact]
    public void Validation_SameIpDifferentPort_Allowed()
    {
        // IP:端口 组合唯一才拦——同 IP 不同端口属两个独立 Modbus 服务，放行
        var config = new PointTableConfig
        {
            Devices = { MakeBareDevice("A", "192.168.1.20"), MakeBareDevice("B", "192.168.1.20", 503) },
        };
        Assert.True(PointTableValidator.Validate(config).Ok);
    }

    [Fact]
    public void Validation_DuplicateDeviceName_Rejected()
    {
        var config = new PointTableConfig
        {
            Devices = { MakeBareDevice("同名牌", "192.168.1.20"), MakeBareDevice("同名牌", "192.168.1.21") },
        };
        var v = PointTableValidator.Validate(config);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("重名"));
    }

    /// <summary>造一台最小设备（1 块 1 位绑定，专供校验器多台闸用例）。</summary>
    private static IoCouplerDevice MakeBareDevice(string name, string ip, int port = 502) =>
        new()
        {
            Name = name,
            Ip = ip,
            Port = port,
            Bindings =
            {
                new IoPointBinding { Block = 1, Bit = 0, IsInput = true,
                    Role = IoPointRole.TrayDetect, Rack = name == "A" ? 1 : 2, Level = 1 },
            },
        };
}
