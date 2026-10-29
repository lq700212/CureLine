// =========================================================================
// PLC 配置测试：自愈 + 往返 + 校验
//
// 锁定的兼容（老现场 ini 无新键也能跑）：
// ① 多机缺省：全启用、全 6060、IP 127.0.0.1；
// ② IP 跳号键沿用（0 号机写 PLCIP2，老 ini/老版本程序互认）。
// 历史：单机 наследие 与 ASCII 协议断言已随单机链路删除（P2.1）。
// 全部临时 ini（TestScope），不碰现场 config。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

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

    // ---------- 配置窗 VM：分段偏移 + 扫描周期（V0.0.22 配置化） ----------

    [Fact(DisplayName = "配置窗VM加载回显偏移与扫描周期")]
    public void PlcConfigVm_Load_ReflectsOffsetsAndScan()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        IniFile.Write(scope.IniPath(), "setting", "ScanIntervalMs", "300");
        AppConfig.Load(scope.IniPath());

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.PlcConfigViewModel(fake);

        Assert.Equal("30,60", vm.DataSegmentOffsetsText);
        Assert.Equal("300", vm.ScanIntervalText);
        Assert.Equal("799", vm.DataLengthText);
    }

    [Fact(DisplayName = "配置窗VM保存写两键且热生效")]
    public void PlcConfigVm_Save_WritesOffsetsAndScan_AppliesInMemory()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        AppConfig.Load(scope.IniPath());

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.PlcConfigViewModel(fake);
        vm.DataSegmentOffsetsText = "45,90";
        vm.ScanIntervalText = "250";
        vm.SaveCommand.Execute(null);

        // 落盘 + 内存静态值同步刷新（保存即热生效：扫描周期无需重连即生效）
        Assert.Equal("45,90", IniFile.ReadStr(AppPaths.ConfigIni, "MES", "DataSegmentOffsets", ""));
        Assert.Equal("250", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "ScanIntervalMs", ""));
        Assert.Equal(new[] { 45, 90 }, MesConfig.DataSegmentOffsets);
        Assert.Equal(250, AppConfig.ScanIntervalMs);
        Assert.True(vm.PlcChanged);
        // 静态恢复由 TestScope 快照兜底（DataSegmentOffsets/ScanIntervalMs 已纳入）
    }

    [Fact(DisplayName = "配置窗VM非法偏移拦保存不落盘")]
    public void PlcConfigVm_BadOffsets_BlocksSave()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        // 先落缺省（模拟现场 ini 已有配置；kernel32 对不存在文件只回缺省不落盘）。
        // 注意 VM 读写走 AppPaths.ConfigIni（TestScope 隔离后的 config\config.ini），
        // 与 scope.IniPath()（TempDir\c.ini）是两个文件，须写前者。
        IniFile.Write(AppPaths.ConfigIni, "MES", "DataSegmentOffsets", "30,60");
        AppConfig.Load();

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.PlcConfigViewModel(fake);
        vm.DataSegmentOffsetsText = "30"; // 只有 1 段 → 拦
        vm.SaveCommand.Execute(null);

        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
        // 拦下未落盘：键值仍是先前写入的缺省（若被覆盖会是 "30"）
        Assert.Equal("30,60", IniFile.ReadStr(AppPaths.ConfigIni, "MES", "DataSegmentOffsets", "?"));
        Assert.False(vm.PlcChanged);
        Assert.Equal(new[] { 30, 60 }, MesConfig.DataSegmentOffsets);
    }
}
