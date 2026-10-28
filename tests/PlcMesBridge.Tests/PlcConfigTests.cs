// =========================================================================
// PLC 配置测试：自愈 + 往返 + 校验
//
// 锁定的兼容（老现场 ini 无新键也能跑）：
// ① 多机缺省：全启用、全 6060、IP 127.0.0.1；
// ② IP 跳号键沿用（0 号机写 PLCIP2，老 ini/老版本程序互认）。
// 历史：单机 наследие 与 ASCII 协议断言已随单机链路删除（P2.1）。
// 全部临时 ini（TestScope），不碰现场 config。
// =========================================================================

using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Tests.Mocks;

namespace PlcMesBridge.Tests;

public class PlcConfigTests
{
    [Fact(DisplayName = "多机存取往返")]
    public void Stations_SaveLoad_RoundTrip()
    {
        using var scope = new TestScope();
        var cs = PlcConfigStore.LoadStations(scope.IniPath());
        cs[0].Ip = "192.168.1.11";
        cs[0].Port = 6061;
        cs[7].Enabled = false;
        PlcConfigStore.SaveStations(scope.IniPath(), cs);
        var r = PlcConfigStore.LoadStations(scope.IniPath());
        Assert.Equal("192.168.1.11", r[0].Ip);
        Assert.Equal(6061, r[0].Port);
        Assert.False(r[7].Enabled);
        // 跳号键沿用：0 号机写 PLCIP2
        Assert.Equal("192.168.1.11", IniFile.ReadStr(scope.IniPath(), "setting", "PLCIP2", ""));
    }

    [Fact(DisplayName = "地址格式只认DR区")]
    public void IsPlcAddress_OnlyDR()
    {
        Assert.True(PlcConfigValidator.IsPlcAddress("D30001"));
        Assert.True(PlcConfigValidator.IsPlcAddress("r13000"));
        Assert.True(PlcConfigValidator.IsPlcAddress("  D10  "));
        Assert.False(PlcConfigValidator.IsPlcAddress("X1"));
        Assert.False(PlcConfigValidator.IsPlcAddress("Y10"));
        Assert.False(PlcConfigValidator.IsPlcAddress("D"));
        Assert.False(PlcConfigValidator.IsPlcAddress(""));
        Assert.False(PlcConfigValidator.IsPlcAddress(null));
    }

    [Fact(DisplayName = "多机校验跳过未启用")]
    public void Stations_Validate_Skips_Disabled()
    {
        var cs = new StationPlcConfig[8];
        for (int i = 0; i < 8; i++)
            cs[i] = new StationPlcConfig { Enabled = false };
        Assert.Empty(PlcConfigValidator.ValidateStations(cs));

        cs[3].Enabled = true;
        cs[3].Ip = "bad ip!!";
        cs[3].Port = 0;
        var errs = PlcConfigValidator.ValidateStations(cs);
        Assert.Equal(2, errs.Count);
    }

    [Fact(DisplayName = "AppConfig.Load带出端口")]
    public void AppConfig_Load_Pulls_Ports()
    {
        using var scope = new TestScope();
        IniFile.Write(scope.IniPath(), "setting", "PLCPorts",
            "6060,6061,6060,6060,6060,6060,6060,6060");
        AppConfig.Load(scope.IniPath());
        Assert.Equal(6061, AppConfig.StationPorts[1]);
    }
}
