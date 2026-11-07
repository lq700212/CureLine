// =========================================================================
// ScannerTcpTests：扫码枪 TCP 模式（V0.0.78 起；V0.0.79 缺省=现场 TCP 预置）
//   ① ScannerOptions：TCP 四键 Load/Save 往返 + 出厂缺省=现场预置（Mode=Tcp、
//      TcpIp=FieldTcpIp、8899、指令空）+ 规范化（仅显式 Serial 走串口、其余回
//      Tcp、端口越界回 8899、HasPortTarget 按方式分桶）；
//   ② ScannerBarcodeService：TCP 启动建 ScannerTcpService 配置（TriggerCommand
//      必须显式透传——绝不能落子模块缺省 "LON"），无 TCP 目标不启动；
//   ③ ScannerConfigViewModel：TCP 编辑区回显/保存往返/校验四件（空 IP/坏端口/
//      触发指令嵌换行）+ 出厂形态=TCP 预置回显 + 启用建议（不落盘）+ 方式切换显隐。
// 假工厂捕获 ScanConfig 断言映射，不碰真实 TCP/串口；全部 TestScope 隔离。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;
using Kaleidoscope.Models;
using Kaleidoscope.Services;
using CureStation.ViewModels;

namespace CureLine.Tests;

public class ScannerTcpTests
{
    private static void EnsureConfigDir() =>
        Directory.CreateDirectory(AppPaths.ConfigDir);

    // ============================================================
    // ① ScannerOptions：配置模型与规范化
    // ============================================================

    [Fact(DisplayName = "扫码枪配置_TCP 四键 ini 往返（Load/Save 含 Mode 规范化）")]
    public void ScannerOptions_TcpKeys_Roundtrip()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Enabled", "1");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Mode", "tcp");       // 大小写不敏感
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TcpIp", "169.254.173.207");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TcpPort", "8899");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TriggerCommand", "LON");

        var o = ScannerOptions.Load();
        Assert.True(o.Enabled);
        Assert.Equal("Tcp", o.NormMode);
        Assert.Equal("169.254.173.207", o.TcpIp);
        Assert.Equal(8899, o.TcpPort);
        Assert.Equal(8899, o.NormTcpPort);
        Assert.Equal("LON", o.TriggerCommand);
        Assert.True(o.HasTcpTarget);
        Assert.True(o.HasPortTarget);        // Tcp 方式按 TcpIp 判

        o.Save();   // 落盘回规范化值，再 Load 应等值
        var back = ScannerOptions.Load();
        Assert.Equal("Tcp", back.NormMode);
        Assert.Equal("169.254.173.207", back.TcpIp);
        Assert.Equal(8899, back.TcpPort);
        Assert.Equal("LON", back.TriggerCommand);
    }

    [Fact(DisplayName = "扫码枪配置_出厂缺省=现场 TCP 预置（Mode=Tcp+DTU IP+8899+指令空，Enabled 仍关）")]
    public void ScannerOptions_Defaults_AreFieldTcpPreset()
    {
        // 无 ini 的机器（内存缺省）与 ini 缺键自愈（Load）必须同款现场形态——
        // 现场教训：手抄 IP 抄错（把 DTU 死配置里的旧上位机地址填进来）连接超时；
        // V0.0.79 起预置值收敛在 FieldTcpIp 一处，配置窗回显即现场口径。
        var fresh = new ScannerOptions();
        Assert.False(fresh.Enabled);                       // 无枪开发机不自动连
        Assert.Equal("Tcp", fresh.NormMode);
        Assert.Equal(ScannerOptions.FieldTcpIp, fresh.TcpIp);
        Assert.Equal(8899, fresh.TcpPort);
        Assert.Equal("", fresh.TriggerCommand);            // 红线：自动连续读码枪不发指令
        Assert.True(fresh.HasTcpTarget);
        Assert.True(fresh.HasPortTarget);

        using var scope = new TestScope();
        EnsureConfigDir();
        var loaded = ScannerOptions.Load();
        Assert.Equal(fresh.NormMode, loaded.NormMode);
        Assert.Equal(fresh.TcpIp, loaded.TcpIp);
        Assert.Equal(fresh.TcpPort, loaded.NormTcpPort);
        Assert.Equal(fresh.TriggerCommand, loaded.TriggerCommand);
        Assert.False(loaded.Enabled);
    }

    [Fact(DisplayName = "扫码枪配置_仅显式 Serial 走串口方式（其余回 Tcp=缺省现场口径）")]
    public void ScannerOptions_ExplicitSerialOnly_WaySerial()
    {
        foreach (var raw in new[] { "Serial", "serial" })
        {
            var o = new ScannerOptions { Mode = raw };
            Assert.Equal("Serial", o.NormMode);
        }
        foreach (var raw in new[] { "Tcp", "tcp", "TCP", "UDP", "", "  ", "x" })
        {
            var o = new ScannerOptions { Mode = raw };
            Assert.Equal("Tcp", o.NormMode);              // 非法/空值落缺省=Tcp
        }
        Assert.Equal("Tcp", new ScannerOptions { Mode = " tcp " }.NormMode);
        Assert.Equal("Serial", new ScannerOptions { Mode = " serial " }.NormMode);
    }

    [Fact(DisplayName = "扫码枪配置_TcpIp 空值 ini 自愈回预置（清空不持久；想不连=切方式/关启用）")]
    public void ScannerOptions_TcpIpEmpty_SelfHealsToPreset()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // kernel32 对"空值键"返回 0 字符 → IniFile.ReadStr 视为缺键 → 回填缺省再返回
        // （IniFile 自愈语义）。TcpIp 清空落盘后，下次 Load 一律回到现场预置值——
        // TcpIp 没有"持久未配置"态，想不连 TCP=切 Serial 方式或关 Enabled。
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TcpIp", "");
        var o = ScannerOptions.Load();
        Assert.Equal(ScannerOptions.FieldTcpIp, o.TcpIp);
        Assert.True(o.HasTcpTarget);
    }

    [Fact(DisplayName = "扫码枪配置_TCP 端口越界回 8899（现场 DTU 现值兜底）")]
    public void ScannerOptions_TcpPortOutOfRange_FallsBackTo8899()
    {
        Assert.Equal(8899, new ScannerOptions { TcpPort = 0 }.NormTcpPort);
        Assert.Equal(8899, new ScannerOptions { TcpPort = 70000 }.NormTcpPort);
        Assert.Equal(8899, new ScannerOptions { TcpPort = -1 }.NormTcpPort);
        Assert.Equal(7645, new ScannerOptions { TcpPort = 7645 }.NormTcpPort);
    }

    [Fact(DisplayName = "扫码枪配置_HasPortTarget 按方式分桶（TCP 看 TcpIp / 串口看定位键）")]
    public void ScannerOptions_HasPortTarget_IsModeAware()
    {
        // Tcp 方式：TcpIp 算目标，串口键不算
        var tcp = new ScannerOptions { Mode = "Tcp", TcpIp = "169.254.173.207" };
        Assert.True(tcp.HasPortTarget);
        Assert.False(tcp.HasSerialTarget);

        var tcpEmptyHost = new ScannerOptions { Mode = "Tcp", TcpIp = "", PortName = "COM3" };
        Assert.False(tcpEmptyHost.HasPortTarget);   // TcpIp 内存态清空：只配了串口对 TCP 无意义

        // Serial 方式：串口定位键算目标，TcpIp 不算
        var serial = new ScannerOptions { Mode = "Serial", PortName = "COM3" };
        Assert.True(serial.HasPortTarget);

        var serialTcpOnly = new ScannerOptions { Mode = "Serial", TcpIp = "169.254.173.207" };
        Assert.False(serialTcpOnly.HasPortTarget);  // 只配了 TcpIp 对串口无意义
    }

    [Fact(DisplayName = "扫码枪配置_SkipReasonText 跳过原因按配置给（null=就绪；启动装配/服务同源文案）")]
    public void ScannerOptions_SkipReasonText_ByConfigState()
    {
        // 未启用：只看 Enabled（缺省件全齐也拦——出厂缺省正是这一态）
        Assert.Equal(ScannerOptions.SkipNotEnabledText,
            new ScannerOptions { Enabled = false }.SkipReasonText());

        // 启用 + TCP 无目标：内存态显式清空才出现（ini 空值已被自愈回预置）
        Assert.Equal(ScannerOptions.SkipNoTargetTcpText,
            new ScannerOptions { Enabled = true, TcpIp = "" }.SkipReasonText());

        // 启用 + TCP 目标就绪（缺省=现场预置 IP）→ null
        Assert.Null(new ScannerOptions { Enabled = true }.SkipReasonText());

        // 启用 + 串口无目标：给串口缺哪一项的文案
        Assert.Equal(ScannerOptions.SkipNoTargetSerialText,
            new ScannerOptions { Enabled = true, Mode = "Serial" }.SkipReasonText());

        // 启用 + 串口定位就绪 → null
        Assert.Null(new ScannerOptions { Enabled = true, Mode = "Serial", PortName = "COM9" }.SkipReasonText());
    }

    // ============================================================
    // ② ScannerBarcodeService：TCP 启动映射（假工厂捕获 ScanConfig）
    // ============================================================

    [Fact(DisplayName = "扫码服务_TCP 模式建 Tcp 配置（TriggerCommand 缺省空=不发，禁落子模块缺省 LON）")]
    public void ScannerService_TcpMode_BuildsTcpConfig()
    {
        using var scope = new TestScope();
        var fake = new FakeScanner();
        ScanConfig? captured = null;
        var service = new ScannerBarcodeService(
            new ScannerOptions { Enabled = true, Mode = "Tcp",
                TcpIp = "169.254.173.207", TcpPort = 8899 },
            cfg => { captured = cfg; return fake; });

        Assert.True(service.Start());
        Assert.Equal("Tcp", captured!.Mode);
        Assert.Equal("169.254.173.207", captured.IpAddress);
        Assert.Equal(8899, captured.Port);
        // 红线：现场枪自动连续读码——缺省必须不发触发指令（子模块 ScanConfig
        // 缺省 "LON" 是基恩士口径，接线层必须覆盖为空串）。
        Assert.Equal("", captured.TriggerCommand);
    }

    [Fact(DisplayName = "扫码服务_TCP 模式配了触发指令则透传（需触发枪的将来路径）")]
    public void ScannerService_TcpMode_TriggerCommandPassthrough()
    {
        using var scope = new TestScope();
        ScanConfig? captured = null;
        using var service = new ScannerBarcodeService(
            new ScannerOptions { Enabled = true, Mode = "Tcp",
                TcpIp = "10.0.0.5", TcpPort = 9004, TriggerCommand = " LON " },
            cfg => { captured = cfg; return new FakeScanner(); });

        Assert.True(service.Start());
        Assert.Equal("LON", captured!.TriggerCommand);   // 去空白透传（尾补 CRLF 由子模块做）
    }

    [Fact(DisplayName = "扫码服务_TCP 模式未配 TcpIp 不启动（工厂不碰；缺省带预置 IP，清空才测此分支）")]
    public void ScannerService_TcpModeWithoutTarget_DoesNotStart()
    {
        using var scope = new TestScope();
        bool factoryCalled = false;
        using var service = new ScannerBarcodeService(
            new ScannerOptions { Enabled = true, Mode = "Tcp", TcpIp = "" },   // 显式清空（缺省=预置 IP）
            _ => { factoryCalled = true; return new FakeScanner(); });

        Assert.False(service.Start());
        Assert.False(factoryCalled);
    }

    [Fact(DisplayName = "扫码服务_Mode 非法值整链按缺省 Tcp 跑（预置 DTU IP 直接生效）")]
    public void ScannerService_InvalidMode_RunsAsTcp()
    {
        using var scope = new TestScope();
        ScanConfig? captured = null;
        using var service = new ScannerBarcodeService(
            new ScannerOptions { Enabled = true, Mode = "UDP", PortName = "COM5" },
            cfg => { captured = cfg; return new FakeScanner(); });

        Assert.True(service.Start());
        Assert.Equal("Tcp", captured!.Mode);          // 非法 Mode 落缺省=Tcp（现场口径）
        Assert.Equal(ScannerOptions.FieldTcpIp, captured.IpAddress);
        Assert.Equal(8899, captured.Port);
    }

    // ============================================================
    // ③ ScannerConfigViewModel：TCP 编辑区
    // ============================================================

    [Fact(DisplayName = "扫码枪配置_TCP 方式回显 ini（勾选框/IP/端口/触发指令 + 区块显隐）")]
    public void ScannerConfigVm_TcpMode_SyncEchoesIni()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Enabled", "1");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "Mode", "Tcp");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TcpIp", "169.254.173.207");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TcpPort", "8899");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "TriggerCommand", "LON");
        IniFile.Write(AppPaths.ConfigIni, "Scanner", "SessionTimeoutSeconds", "30");

        var vm = new ScannerConfigViewModel(new FakeCureDialogService());

        Assert.True(vm.EditEnabled);
        Assert.True(vm.EditUseTcp);
        Assert.True(vm.ShowTcpSection);
        Assert.False(vm.ShowSerialSection);   // 两区块互斥
        Assert.Equal("169.254.173.207", vm.EditTcpIp);
        Assert.Equal("8899", vm.EditTcpPort);
        Assert.Equal("LON", vm.EditTriggerCommand);
        Assert.Equal("30", vm.EditSessionTimeout);
    }

    [Fact(DisplayName = "扫码枪配置_TCP 方式保存往返（Mode=Tcp + 三件落盘）")]
    public void ScannerConfigVm_TcpSave_Roundtrip()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditUseTcp = true,
            EditTcpIp = "169.254.173.207",
            EditTcpPort = "8899",
            EditTriggerCommand = "",           // 现场口径：留空不发
            EditSessionTimeout = "20",
        };
        bool? requestClose = null;
        vm.RequestClose += ok => requestClose = ok;

        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        Assert.True(requestClose);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("Msg:"));   // 无失败提示

        var saved = ScannerOptions.Load();
        Assert.Equal("Tcp", saved.NormMode);
        Assert.Equal("169.254.173.207", saved.TcpIp);
        Assert.Equal(8899, saved.TcpPort);
        Assert.Equal("", saved.TriggerCommand);
        Assert.Equal(20, saved.SessionTimeoutSeconds);
    }

    [Fact(DisplayName = "扫码枪配置_TCP 方式空 IP 拦下（提示去 DTU 配置台确认）")]
    public void ScannerConfigVm_TcpSave_MissingIpBlocked()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditUseTcp = true,
            EditTcpIp = "   ",
        };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("未填 TCP 目标"));
        Assert.False(ScannerOptions.Load().Enabled);   // 未写盘
    }

    [Fact(DisplayName = "扫码枪配置_TCP 方式坏端口拦下（1~65535 之外）")]
    public void ScannerConfigVm_TcpSave_BadPortBlocked()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditUseTcp = true,
            EditTcpIp = "169.254.173.207",
            EditTcpPort = "70000",
        };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        string message = fake.Calls.Single(c => c.StartsWith("Msg:"));
        Assert.Contains("TCP 端口", message);
        Assert.False(ScannerOptions.Load().Enabled);
    }

    [Fact(DisplayName = "扫码枪配置_TCP 方式触发指令嵌换行拦下（发送会拆成多条指令）")]
    public void ScannerConfigVm_TcpSave_TriggerWithLineBreakBlocked()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new ScannerConfigViewModel(fake)
        {
            EditEnabled = true,
            EditUseTcp = true,
            EditTcpIp = "169.254.173.207",
            EditTriggerCommand = "LO\rN",
        };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("触发指令"));
        Assert.False(ScannerOptions.Load().Enabled);
    }

    [Fact(DisplayName = "扫码枪配置_方式切换互斥显隐（缺省 TCP；切串口显串口区，切回显 TCP 区）")]
    public void ScannerConfigVm_ToggleSwitchesSections()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var vm = new ScannerConfigViewModel(new FakeCureDialogService());

        // 出厂缺省 = TCP（现场现行口径，V0.0.79 缺省值翻转）
        Assert.True(vm.EditUseTcp);
        Assert.True(vm.ShowTcpSection);
        Assert.False(vm.ShowSerialSection);

        vm.EditUseTcp = false;
        Assert.True(vm.ShowSerialSection);
        Assert.False(vm.ShowTcpSection);

        vm.EditUseTcp = true;
        Assert.False(vm.ShowSerialSection);
        Assert.True(vm.ShowTcpSection);
    }

    // ============================================================
    // 假扫码枪（同 SlotBindingTests 口径：Open 即连上，事件手工触发）
    // ============================================================

    private sealed class FakeScanner : IScanner
    {
        public event EventHandler<string>? SerialNumberScanned;
        public event EventHandler<bool>? ConnectionChanged;

        public bool IsOpen { get; private set; }
        public string Name => "FAKE";
        public bool Disposed { get; private set; }

        public bool Open()
        {
            IsOpen = true;
            ConnectionChanged?.Invoke(this, true);
            return true;
        }

        public bool SendTrigger() => true;
        public void Emit(string code) => SerialNumberScanned?.Invoke(this, code);

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            IsOpen = false;
            ConnectionChanged?.Invoke(this, false);
        }
    }
}
