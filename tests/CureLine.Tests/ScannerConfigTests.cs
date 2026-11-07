// =========================================================================
// ScannerConfigTests：扫码枪配置窗 VM（V0.0.77，dev 开发者专属）
//   ① 打开回显 ini 现值（停止位 15↔1.5 显示映射）；
//   ② 首次未配置的现场预置（启用 + 设备关键字 USB，不落盘）；
//   ③ 保存校验与落盘（合法值往返 / 启用但无串口定位拦下 / 坏值拦下）；
//   ④ 尊重已配置的 Enabled=0（不强行预填启用）；
//   ⑤ 检测串口（枚举系统 COM 号，不依赖真机）。
// 全部 TestScope 隔离临时目录，不碰本机 config.ini。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;
using CureStation.ViewModels;

namespace CureLine.Tests;

public class ScannerConfigTests
{
    private static void EnsureConfigDir() =>
        Directory.CreateDirectory(AppPaths.ConfigDir);

    [Fact(DisplayName = "扫码枪配置_打开回显 ini 现值（停止位 15→1.5）")]
    public void ScannerConfigVm_Sync_EchoesIni()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Enabled", "1");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "PortName", "COM3");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "DeviceKeyword", "CH340");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "BaudRate", "115200");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "DataBits", "7");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "StopBits", "15");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Parity", "Even");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "SessionTimeoutSeconds", "30");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "DebugLog", "1");

        var vm = new ScannerConfigViewModel(new FakeCureDialogService());

        Assert.True(vm.EditEnabled);
        Assert.Equal("COM3", vm.EditPortName);
        Assert.Equal("CH340", vm.EditDeviceKeyword);
        Assert.Equal("115200", vm.EditBaudRate);
        Assert.Equal(7, vm.EditDataBits);
        Assert.Equal("1.5", vm.EditStopBits);   // 项目存 "15"，界面显示 1.5
        Assert.Equal("Even", vm.EditParity);
        Assert.Equal("30", vm.EditSessionTimeout);
        Assert.True(vm.EditDebugLog);
    }

    [Fact(DisplayName = "扫码枪配置_首次未配置按现场预置（启用+USB，不落盘）")]
    public void ScannerConfigVm_FirstOpen_PrefillsFieldValues()
    {
        using var scope = new TestScope();
        EnsureConfigDir();   // 目录在、ini 不存在 = 首次运行

        var vm = new ScannerConfigViewModel(new FakeCureDialogService());

        Assert.True(vm.EditEnabled);              // 现场接了枪：默认启用
        Assert.Equal("USB", vm.EditDeviceKeyword); // SH-5000-GHD 经 USB 转接线
        Assert.Equal("", vm.EditPortName);         // COM 号让现场"检测串口"选
        Assert.Equal("9600", vm.EditBaudRate);     // 常见出厂值
        Assert.False(ScannerOptions.Load().Enabled);   // 预填不落盘，点保存才确认
    }

    [Fact(DisplayName = "扫码枪配置_保存合法值落盘并请求关窗")]
    public void ScannerConfigVm_Save_PersistsAndRequestsClose()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditPortName = "COM7",
            EditBaudRate = "115200",
            EditDataBits = 7,
            EditStopBits = "2",
            EditParity = "Odd",
            EditSessionTimeout = "30",
            EditDebugLog = true,
        };
        bool? requestClose = null;
        vm.RequestClose += ok => requestClose = ok;

        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        Assert.True(requestClose);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("Msg:"));   // 无失败提示

        var saved = ScannerOptions.Load();
        Assert.True(saved.Enabled);
        Assert.Equal("COM7", saved.PortName);
        Assert.Equal("USB", saved.DeviceKeyword);   // 预置值一并保存
        Assert.Equal(115200, saved.BaudRate);
        Assert.Equal(7, saved.DataBits);
        Assert.Equal("2", saved.StopBits);
        Assert.Equal("Odd", saved.Parity);
        Assert.Equal(30, saved.SessionTimeoutSeconds);
        Assert.True(saved.DebugLog);
    }

    [Fact(DisplayName = "扫码枪配置_启用但无串口定位拦下（不落盘）")]
    public void ScannerConfigVm_EnabledWithoutPortTarget_Blocks()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditPortName = "",
            EditDeviceKeyword = "",
        };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("未填串口定位"));
        Assert.False(ScannerOptions.Load().Enabled);   // 未写盘
    }

    [Fact(DisplayName = "扫码枪配置_坏值一次列全并拦下（COM 号/波特率/超时）")]
    public void ScannerConfigVm_InvalidValues_Block()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditPortName = "COMX",
            EditDeviceKeyword = "",
            EditBaudRate = "100",
            EditSessionTimeout = "3",
        };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        string message = fake.Calls.Single(c => c.StartsWith("Msg:"));
        Assert.Contains("格式无效", message);
        Assert.Contains("波特率", message);
        Assert.Contains("超时", message);
        Assert.False(ScannerOptions.Load().Enabled);
    }

    [Fact(DisplayName = "扫码枪配置_已配置定位且 Enabled=0 时尊重现值（不预填启用）")]
    public void ScannerConfigVm_ExistingDisabled_RespectsValue()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Enabled", "0");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "PortName", "COM3");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "DeviceKeyword", "");

        var vm = new ScannerConfigViewModel(new FakeCureDialogService());

        Assert.False(vm.EditEnabled);          // 现场明确关了：不强行打开
        Assert.Equal("COM3", vm.EditPortName); // 定位原样回显
        Assert.Equal("", vm.EditDeviceKeyword);
    }

    [Fact(DisplayName = "扫码枪配置_已配 Enabled=0 但定位全空=视为未配置（给启用+USB建议）")]
    public void ScannerConfigVm_DisabledWithoutTarget_PrefillsPreset()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // ini 自愈缺省就是这种形态：Enabled=0 + 两种定位都空
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Enabled", "0");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "PortName", "");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "DeviceKeyword", "");

        var vm = new ScannerConfigViewModel(new FakeCureDialogService());

        Assert.True(vm.EditEnabled);                 // 从未配过枪 → 给出启用建议
        Assert.Equal("USB", vm.EditDeviceKeyword);   // 现场转接线虚拟串口关键字
        Assert.Equal("", vm.EditPortName);
        Assert.False(ScannerOptions.Load().Enabled); // 建议值不落盘，点保存才确认
    }

    [Fact(DisplayName = "扫码枪配置_检测串口枚举系统 COM 号不抛异常")]
    public void ScannerConfigVm_DetectPorts_ListsSystemPorts()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Enabled", "0");
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake);

        vm.DetectPortsCommand.Execute(null);   // 开发机有串口/无串口都必须安全

        string[] systemPorts = System.IO.Ports.SerialPort.GetPortNames();
        Assert.Equal(systemPorts.Length, vm.DetectedPorts.Count);
        Assert.All(vm.DetectedPorts, p => Assert.Contains(p, systemPorts));

        // 唯一一个口时自动回填 PortName（无口则不填，也不弹错之外的提示）
        if (systemPorts.Length == 1)
            Assert.Equal(systemPorts[0], vm.EditPortName);
    }
}
