// =========================================================================
// PLC 配置测试：自愈 + 往返 + 校验
//
// 锁定的兼容（老现场 ini 无新键也能跑）：
// ① 多机缺省：全启用、全 6060、IP 127.0.0.1；
// ② IP 跳号键沿用（0 号机写 StationIp1，老 ini/老版本程序互认）。
// 历史：单机 наследие 与 ASCII 协议断言已随单机链路删除（P2.1）。
// 全部临时 ini（TestScope），不碰现场 config。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class StationConfigTests
{
    [Fact(DisplayName = "多机存取往返")]
    public void Stations_SaveLoad_RoundTrip()
    {
        using var scope = new TestScope();
        var cs = StationConfigStore.LoadStations(scope.IniPath());
        cs[0].Ip = "192.168.1.11";
        cs[0].Port = 6061;
        cs[7].Enabled = false;
        StationConfigStore.SaveStations(scope.IniPath(), cs);
        var r = StationConfigStore.LoadStations(scope.IniPath());
        Assert.Equal("192.168.1.11", r[0].Ip);
        Assert.Equal(6061, r[0].Port);
        Assert.False(r[7].Enabled);
        // 跳号键沿用：0 号机写 StationIp1
        Assert.Equal("192.168.1.11", IniFile.ReadStr(scope.IniPath(), "setting", "StationIp1", ""));
    }

    [Fact(DisplayName = "地址格式只认DR区")]
    public void IsStationAddress_OnlyDR()
    {
        Assert.True(StationConfigValidator.IsStationAddress("D30001"));
        Assert.True(StationConfigValidator.IsStationAddress("r13000"));
        Assert.True(StationConfigValidator.IsStationAddress("  D10  "));
        Assert.False(StationConfigValidator.IsStationAddress("X1"));
        Assert.False(StationConfigValidator.IsStationAddress("Y10"));
        Assert.False(StationConfigValidator.IsStationAddress("D"));
        Assert.False(StationConfigValidator.IsStationAddress(""));
        Assert.False(StationConfigValidator.IsStationAddress(null));
    }

    [Fact(DisplayName = "多机校验跳过未启用")]
    public void Stations_Validate_Skips_Disabled()
    {
        var cs = new StationConfig[8];
        for (int i = 0; i < 8; i++)
            cs[i] = new StationConfig { Enabled = false, Name = "测试机台" };
        Assert.Empty(StationConfigValidator.ValidateStations(cs));

        cs[3].Enabled = true;
        cs[3].Ip = "bad ip!!";
        cs[3].Port = 0;
        var errs = StationConfigValidator.ValidateStations(cs);
        Assert.Equal(2, errs.Count);
    }

    [Fact(DisplayName = "AppConfig.Load带出端口")]
    public void AppConfig_Load_Pulls_Ports()
    {
        using var scope = new TestScope();
        IniFile.Write(scope.IniPath(), "setting", "StationPorts",
            "6060,6061,6060,6060,6060,6060,6060,6060");
        AppConfig.Load(scope.IniPath());
        Assert.Equal(6061, AppConfig.StationPorts[1]);
    }

    // ---------- 机台名配置化（V0.0.32：ini [StationNames] 节键 1..8） ----------

    [Fact(DisplayName = "机台名缺省回填缺键与空值")]
    public void StationNames_MissingOrEmpty_FallBackDefaults()
    {
        using var scope = new TestScope();
        // ini 完全没有 [StationNames] 节：8 台全部回落老项目缺省名
        var cs = StationConfigStore.LoadStations(scope.IniPath());
        Assert.Equal(AppConfig.DefaultStationNames[0], cs[0].Name);
        Assert.Equal(AppConfig.DefaultStationNames[7], cs[7].Name);
        // 只配了 1 号机且 2 号机为空白：1 号用配置值，2 号回落缺省
        IniFile.Write(scope.IniPath(), "StationNames", "1", "贴片机A线");
        IniFile.Write(scope.IniPath(), "StationNames", "2", "   ");
        var r = StationConfigStore.LoadStations(scope.IniPath());
        Assert.Equal("贴片机A线", r[0].Name);
        Assert.Equal(AppConfig.DefaultStationNames[1], r[1].Name);
    }

    [Fact(DisplayName = "机台名保存读取往返")]
    public void StationNames_SaveLoad_RoundTrip()
    {
        using var scope = new TestScope();
        var cs = StationConfigStore.LoadStations(scope.IniPath());
        cs[2].Name = "回流焊检查机";
        StationConfigStore.SaveStations(scope.IniPath(), cs);
        Assert.Equal("回流焊检查机",
            IniFile.ReadStr(scope.IniPath(), "StationNames", "3", ""));
        var r = StationConfigStore.LoadStations(scope.IniPath());
        Assert.Equal("回流焊检查机", r[2].Name);
    }

    [Fact(DisplayName = "AppConfig.Load带出机台名且MesLogger同源")]
    public void AppConfig_Load_Pulls_StationNames_AndMesLoggerFollows()
    {
        using var scope = new TestScope();
        // 节键 1..8 对应第 1..8 台：键 "6" = 下标 5（第 6 台）
        IniFile.Write(scope.IniPath(), "StationNames", "6", "光效测试线");
        AppConfig.Load(scope.IniPath());
        Assert.Equal("光效测试线", AppConfig.StationNames[5]);
        // MesLogger.StationNames 委托 AppConfig——面板名/视图下拉/日志文件名三处同源
        Assert.Equal("光效测试线", MesLogger.StationNames[5]);
        Assert.Equal("光效测试线", MesLogger.GenLogName(5));
    }

    [Fact(DisplayName = "机台名校验：非空且无文件名非法字符")]
    public void StationName_Validation()
    {
        Assert.True(StationConfigValidator.IsStationNameValid("贴片机A线"));
        Assert.False(StationConfigValidator.IsStationNameValid(""));
        Assert.False(StationConfigValidator.IsStationNameValid("   "));
        Assert.False(StationConfigValidator.IsStationNameValid("A/B"));   // 路径分隔符
        Assert.False(StationConfigValidator.IsStationNameValid("A:B"));   // 盘符
        Assert.False(StationConfigValidator.IsStationNameValid(new string('长', 51)));
        Assert.True(StationConfigValidator.IsStationNameValid(new string('长', 50)));
    }

    // ---------- 配置窗 VM：分段偏移 + 扫描周期（V0.0.22 配置化） ----------

    [Fact(DisplayName = "配置窗VM加载回显偏移与扫描周期")]
    public void GatewayConfigVm_Load_ReflectsOffsetsAndScan()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        IniFile.Write(scope.IniPath(), "setting", "ScanIntervalMs", "300");
        AppConfig.Load(scope.IniPath());

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.GatewayConfigViewModel(fake);

        Assert.Equal("30,60", vm.DataSegmentOffsetsText);
        Assert.Equal("300", vm.ScanIntervalText);
        Assert.Equal("799", vm.DataLengthText);
    }

    [Fact(DisplayName = "配置窗VM保存写两键且热生效")]
    public void GatewayConfigVm_Save_WritesOffsetsAndScan_AppliesInMemory()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        AppConfig.Load(scope.IniPath());

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.GatewayConfigViewModel(fake);
        vm.DataSegmentOffsetsText = "45,90";
        vm.ScanIntervalText = "250";
        vm.SaveCommand.Execute(null);

        // 落盘 + 内存静态值同步刷新（保存即热生效：扫描周期无需重连即生效）
        Assert.Equal("45,90", IniFile.ReadStr(AppPaths.ConfigIni, "MES", "DataSegmentOffsets", ""));
        Assert.Equal("250", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "ScanIntervalMs", ""));
        Assert.Equal(new[] { 45, 90 }, MesConfig.DataSegmentOffsets);
        Assert.Equal(250, AppConfig.ScanIntervalMs);
        Assert.True(vm.ConfigChanged);
        // 静态恢复由 TestScope 快照兜底（DataSegmentOffsets/ScanIntervalMs 已纳入）
    }

    [Fact(DisplayName = "配置窗VM改名保存落盘并热生效")]
    public void GatewayConfigVm_Rename_Save_LandsAndApplies()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        AppConfig.Load(scope.IniPath());

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.GatewayConfigViewModel(fake);
        // 加载回显：行内名字框=当前生效名（缺省回填后非空）
        Assert.Equal(AppConfig.DefaultStationNames[0], vm.Stations[0].Name);
        vm.Stations[0].Name = "贴片机B线";
        vm.SaveCommand.Execute(null);

        // 落盘 [StationNames] 节 + AppConfig 内存同步刷新（重启后仍生效）
        Assert.Equal("贴片机B线", IniFile.ReadStr(AppPaths.ConfigIni, "StationNames", "1", ""));
        Assert.Equal("贴片机B线", AppConfig.StationNames[0]);
        Assert.Equal("贴片机B线", MesLogger.StationNames[0]);
        Assert.True(vm.ConfigChanged);
    }

    [Fact(DisplayName = "配置窗VM非法机台名拦保存")]
    public void GatewayConfigVm_BadName_BlocksSave()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        AppConfig.Load(scope.IniPath());

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.GatewayConfigViewModel(fake);
        vm.Stations[0].Name = "A/B线"; // 文件名非法字符 → 拦
        vm.SaveCommand.Execute(null);

        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
        Assert.False(vm.ConfigChanged);
        // 拦下未落盘：[StationNames] 节没建，读不到键回缺省 "?"
        Assert.Equal("?", IniFile.ReadStr(AppPaths.ConfigIni, "StationNames", "1", "?"));
    }

    [Fact(DisplayName = "配置窗VM非法偏移拦保存不落盘")]
    public void GatewayConfigVm_BadOffsets_BlocksSave()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigIni)!);
        // 先落缺省（模拟现场 ini 已有配置；kernel32 对不存在文件只回缺省不落盘）。
        // 注意 VM 读写走 AppPaths.ConfigIni（TestScope 隔离后的 config\config.ini），
        // 与 scope.IniPath()（TempDir\c.ini）是两个文件，须写前者。
        IniFile.Write(AppPaths.ConfigIni, "MES", "DataSegmentOffsets", "30,60");
        AppConfig.Load();

        var fake = new FakeDialogService();
        var vm = new CimGateway.ViewModels.GatewayConfigViewModel(fake);
        vm.DataSegmentOffsetsText = "30"; // 只有 1 段 → 拦
        vm.SaveCommand.Execute(null);

        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
        // 拦下未落盘：键值仍是先前写入的缺省（若被覆盖会是 "30"）
        Assert.Equal("30,60", IniFile.ReadStr(AppPaths.ConfigIni, "MES", "DataSegmentOffsets", "?"));
        Assert.False(vm.ConfigChanged);
        Assert.Equal(new[] { 30, 60 }, MesConfig.DataSegmentOffsets);
    }
}
