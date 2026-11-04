// =========================================================================
// 软件授权测试（LicenseCodec + LicenseGate + LicenseActivationViewModel）
//
// 覆盖：双产品设备码/产品隔离、Base32 往返、payload 字段、ECDsa 验签、
//   激活落盘/重启恢复、试用到期、时钟回拨、挑战值防重放、指纹不符、
//   演示模式放行、激活窗 VM 编排。
//
// 全部用临时 ini + 假指纹 + 内存水位 + 本用例临时密钥对，不碰真机 WMI/
//   注册表/现场 config；不调用 LicenseGate.Default（它采真机指纹）。
// 密钥对每个用例生成一次（P-256 生成约几毫秒，用例数克制）。
// =========================================================================

using System.Security.Cryptography;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.ViewModels;

namespace CureLine.Tests;

public class LicenseTests : IDisposable
{
    private readonly string _tmp;
    private readonly ECDsa _signer;
    private readonly string _fp = "11223344";
    private readonly string _ch = "AABBCCDD";

    public LicenseTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "CureLineLic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_tmp, true); } catch { }
    }

    // ════════════════ 测试夹具 ════════════════

    private byte[] Sign(byte[] payload) =>
        _signer.SignData(payload, HashAlgorithmName.SHA256);

    private bool Verify(byte[] payload, byte[] signature) =>
        _signer.VerifyData(payload, signature, HashAlgorithmName.SHA256);

    /// <summary>用临时私钥签一个激活码（fp8/ch8 8 位 hex）。</summary>
    private string MakeCode(ushort productId, string fp8, string ch8,
        byte type = LicenseCodec.TypePermanent, byte days = 0, DateTime? start = null)
    {
        byte[] payload = LicenseCodec.BuildPayload(
            productId, LicenseCodec.HexToBytes(fp8), LicenseCodec.HexToBytes(ch8),
            type, days, LicenseCodec.DateToDays(start ?? DateTime.Now));
        return LicenseCodec.EncodeActivationCode(payload, Sign(payload));
    }

    private LicenseGate NewGate(
        ushort productId = LicenseCodec.ProductCureLine,
        Func<DateTime>? now = null,
        IFingerprintProvider? fingerprint = null,
        ILicenseWaterMarkStore? waterMark = null,
        Func<bool>? simulate = null,
        string ini = "config.ini",
        DateTime? builtOn = null)
    {
        return new LicenseGate(
            Path.Combine(_tmp, ini),
            fingerprint ?? new FixedFingerprint(_fp),
            now ?? (() => new DateTime(2026, 11, 4, 10, 0, 0)),
            Verify,
            waterMark ?? new MemoryWaterMark(),
            simulate,
            builtOn ?? new DateTime(2020, 1, 1),
            null,
            productId,
            LicenseCodec.GetProductName(productId));
    }

    private sealed class FixedFingerprint(string fp8, bool ok = true) : IFingerprintProvider
    {
        public FingerprintResult Get() =>
            ok ? new FingerprintResult(true, fp8, "") : new FingerprintResult(false, "", "模拟采集失败");
    }

    private sealed class MemoryWaterMark : ILicenseWaterMarkStore
    {
        public DateTime Value { get; set; } = DateTime.MinValue;
        public DateTime Read() => Value;
        public void Write(DateTime value) => Value = value;
    }

    // ════════════════ 编解码 ════════════════

    [Fact(DisplayName = "设备码双产品前缀/校验位互不相同且可回读")]
    public void DeviceCode_BothProducts_RoundTrip()
    {
        string cure = LicenseCodec.BuildDeviceCode(LicenseCodec.ProductCureLine, _fp, _ch);
        string hj = LicenseCodec.BuildDeviceCode(LicenseCodec.ProductHuaJiVision, _fp, _ch);

        Assert.StartsWith("CURE-", cure);
        Assert.StartsWith("HUAJI-", hj);
        // 前缀纳入校验位哈希 → 同指纹/挑战的设备码校验位必不同
        Assert.NotEqual(cure[^4..], hj[^4..]);

        Assert.True(LicenseCodec.TryParseDeviceCode(cure, out ushort p1, out string f1, out string c1));
        Assert.Equal(LicenseCodec.ProductCureLine, p1);
        Assert.Equal(_fp, f1);
        Assert.Equal(_ch, c1);

        Assert.True(LicenseCodec.TryParseDeviceCode(hj, out ushort p2, out string f2, out string c2));
        Assert.Equal(LicenseCodec.ProductHuaJiVision, p2);
        Assert.Equal(_fp, f2);
        Assert.Equal(_ch, c2);
    }

    [Fact(DisplayName = "HUAJI 校验位与旧版逐字节一致（历史设备码兼容）")]
    public void DeviceCode_HuaJiChecksum_MatchesLegacy()
    {
        // 旧版硬编码 SHA256("HUAJI1"+fp+ch) 前 2 字节——固定向量锁死兼容性
        Assert.Equal("97B2", LicenseCodec.DeviceChecksum(LicenseCodec.ProductHuaJiVision, _fp, _ch));
    }

    [Fact(DisplayName = "设备码宽容大小写/空白/混淆字符")]
    public void DeviceCode_ToleratesHandwriting()
    {
        string code = LicenseCodec.BuildDeviceCode(LicenseCodec.ProductCureLine, _fp, _ch);
        string messy = " " + code.ToLowerInvariant().Replace("-", " - ") + "\r\n";
        Assert.True(LicenseCodec.TryParseDeviceCode(messy, out ushort pid, out string fp, out string ch));
        Assert.Equal(LicenseCodec.ProductCureLine, pid);
        Assert.Equal(_fp, fp);
        Assert.Equal(_ch, ch);

        // 校验位抄错一位必须拦下
        string bad = code[..^1] + (code[^1] == '0' ? '1' : '0');
        Assert.False(LicenseCodec.TryParseDeviceCode(bad, out _, out _, out _));
        // 未知前缀
        Assert.False(LicenseCodec.TryParseDeviceCode("OTHER-" + code[5..], out _, out _, out _));
    }

    [Fact(DisplayName = "Base32 往返任意字节无损")]
    public void Base32_RoundTrip()
    {
        for (int len = 1; len <= 80; len += 7)
        {
            byte[] data = new byte[len];
            RandomNumberGenerator.Fill(data);
            byte[] back = LicenseCodec.Base32Decode(LicenseCodec.Base32Encode(data))!;
            // 编码端补零位，解码可能多一个 0 字节（80 字节以内 5bit 对齐时不会）
            Assert.Equal(data, back);
        }
        Assert.Null(LicenseCodec.Base32Decode("U"));   // U 不在 Crockford 字母表
    }

    [Fact(DisplayName = "激活码编解码+验签+payload 字段全对")]
    public void ActivationCode_RoundTrip()
    {
        string code = MakeCode(LicenseCodec.ProductCureLine, _fp, _ch,
            LicenseCodec.TypeTrial, 30, new DateTime(2026, 11, 4));

        Assert.True(LicenseCodec.TryDecodeActivationCode(code, out byte[] payload, out byte[] sig));
        Assert.True(Verify(payload, sig));
        Assert.True(LicenseCodec.TryParsePayload(payload, out ushort pid, out byte[] fp4,
            out byte[] ch4, out byte type, out byte days, out uint startDays));

        Assert.Equal(LicenseCodec.ProductCureLine, pid);
        Assert.Equal(_fp, LicenseCodec.BytesToHex(fp4));
        Assert.Equal(_ch, LicenseCodec.BytesToHex(ch4));
        Assert.Equal(LicenseCodec.TypeTrial, type);
        Assert.Equal(30, days);
        Assert.Equal(new DateTime(2026, 11, 4), LicenseCodec.DaysToDate(startDays));
    }

    [Fact(DisplayName = "篡改 payload 后验签必失败")]
    public void ActivationCode_Tamper_Rejected()
    {
        string code = MakeCode(LicenseCodec.ProductCureLine, _fp, _ch);
        Assert.True(LicenseCodec.TryDecodeActivationCode(code, out byte[] payload, out byte[] sig));
        payload[9] = 99;                       // 改试用天数
        Assert.False(Verify(payload, sig));
    }

    [Fact(DisplayName = "payload 产品 ID 大端写入")]
    public void Payload_ProductId_BigEndian()
    {
        byte[] p = LicenseCodec.BuildPayload(LicenseCodec.ProductCureLine,
            LicenseCodec.HexToBytes(_fp), LicenseCodec.HexToBytes(_ch),
            LicenseCodec.TypePermanent, 0, 0);
        Assert.Equal(0x43, p[14]);
        Assert.Equal(0x4C, p[15]);

        byte[] h = LicenseCodec.BuildPayload(LicenseCodec.ProductHuaJiVision,
            LicenseCodec.HexToBytes(_fp), LicenseCodec.HexToBytes(_ch),
            LicenseCodec.TypePermanent, 0, 0);
        Assert.Equal(0xFF, h[14]);
        Assert.Equal(0xFF, h[15]);
    }

    // ════════════════ 门禁 ════════════════

    [Fact(DisplayName = "无记录=未激活且不放行")]
    public void Gate_NoRecord_NotActivated()
    {
        var gate = NewGate();
        gate.Refresh();
        Assert.Equal(LicenseState.NotActivated, gate.CurrentState);
        Assert.False(gate.IsProductionAllowed());
        Assert.Equal("授权：未激活", gate.ShortStatusText());
    }

    [Fact(DisplayName = "永久激活落盘，重启后自动恢复")]
    public void Gate_ActivatePermanent_PersistsAcrossRestart()
    {
        var gate = NewGate();
        gate.Refresh();
        string code = MakeCode(LicenseCodec.ProductCureLine, _fp, gate.CurrentChallenge());

        Assert.Null(gate.TryActivate(code));
        Assert.Equal(LicenseState.Permanent, gate.CurrentState);
        Assert.True(gate.IsProductionAllowed());
        Assert.Equal("授权：永久", gate.ShortStatusText());

        // 模拟重启：同一 ini + 同一指纹的新实例（挑战值全新）仍认永久授权
        var restarted = NewGate();
        restarted.Refresh();
        Assert.Equal(LicenseState.Permanent, restarted.CurrentState);
        Assert.True(restarted.IsProductionAllowed());
    }

    [Fact(DisplayName = "产品隔离：HJVision 码不能激活 CureLine，反之亦然")]
    public void Gate_ProductIsolation_BothDirections()
    {
        var cureGate = NewGate(LicenseCodec.ProductCureLine);
        cureGate.Refresh();
        string hjCode = MakeCode(LicenseCodec.ProductHuaJiVision, _fp, cureGate.CurrentChallenge());
        string? err = cureGate.TryActivate(hjCode);
        Assert.NotNull(err);
        Assert.Contains("HJVision", err);
        Assert.Equal(LicenseState.NotActivated, cureGate.CurrentState);

        var hjGate = NewGate(LicenseCodec.ProductHuaJiVision, ini: "hj.ini");
        hjGate.Refresh();
        string cureCode = MakeCode(LicenseCodec.ProductCureLine, _fp, hjGate.CurrentChallenge());
        err = hjGate.TryActivate(cureCode);
        Assert.NotNull(err);
        Assert.Contains("CureLine", err);
        Assert.Equal(LicenseState.NotActivated, hjGate.CurrentState);
    }

    [Fact(DisplayName = "挑战值不符（旧码重放）被拒")]
    public void Gate_ChallengeMismatch_Rejected()
    {
        var gate = NewGate();
        gate.Refresh();
        string oldCode = MakeCode(LicenseCodec.ProductCureLine, _fp, gate.CurrentChallenge());

        // 窗口重开 = 新挑战值；旧码 payload 里还是旧挑战
        gate.NewChallenge();
        string? err = gate.TryActivate(oldCode);
        Assert.NotNull(err);
        Assert.Contains("设备码已刷新", err);
        Assert.Equal(LicenseState.NotActivated, gate.CurrentState);
    }

    [Fact(DisplayName = "指纹不符：激活拒绝、记录态报 FingerprintError")]
    public void Gate_FingerprintMismatch_Rejected()
    {
        var gate = NewGate();
        gate.Refresh();
        gate.NewChallenge();
        string otherMachine = MakeCode(LicenseCodec.ProductCureLine, "DEADBEEF", gate.CurrentChallenge());
        string? err = gate.TryActivate(otherMachine);
        Assert.NotNull(err);
        Assert.Contains("设备指纹不符", err);
        Assert.Equal(LicenseState.NotActivated, gate.CurrentState);

        // 已落盘记录中的指纹与本机不符（整目录克隆场景）
        string ini = Path.Combine(_tmp, "config.ini");
        IniFile.Write(ini, "License", "Fingerprint", "DEADBEEF");
        IniFile.Write(ini, "License", "LicenseCode", otherMachine);
        gate.Refresh();
        Assert.Equal(LicenseState.FingerprintError, gate.CurrentState);
        Assert.False(gate.IsProductionAllowed());
    }

    [Fact(DisplayName = "试用到期日当天可用，次日过期")]
    public void Gate_TrialExpiry()
    {
        DateTime day = new(2026, 11, 4);
        var gate = NewGate(now: () => day);
        gate.Refresh();
        string code = MakeCode(LicenseCodec.ProductCureLine, _fp, gate.CurrentChallenge(),
            LicenseCodec.TypeTrial, 30, day);
        Assert.Null(gate.TryActivate(code));
        Assert.Equal(LicenseState.Trial, gate.CurrentState);
        Assert.Equal("授权：试用剩 30 天", gate.ShortStatusText());

        // 到期日当天（day+30）仍可用
        var lastDay = NewGate(now: () => day.AddDays(30), waterMark: new MemoryWaterMark { Value = day });
        lastDay.Refresh();
        Assert.Equal(LicenseState.Trial, lastDay.CurrentState);

        // 到期次日过期
        var expired = NewGate(now: () => day.AddDays(31), waterMark: new MemoryWaterMark { Value = day });
        expired.Refresh();
        Assert.Equal(LicenseState.Expired, expired.CurrentState);
        Assert.False(expired.IsProductionAllowed());
    }

    [Fact(DisplayName = "时钟回拨：试用按过期处理，永久不受影响")]
    public void Gate_ClockRollback()
    {
        DateTime signedDay = new(2026, 11, 4);
        var trialGate = NewGate(now: () => signedDay);
        trialGate.Refresh();
        string trial = MakeCode(LicenseCodec.ProductCureLine, _fp, trialGate.CurrentChallenge(),
            LicenseCodec.TypeTrial, 30, signedDay);
        Assert.Null(trialGate.TryActivate(trial));

        // 把系统时间拨回 10 天（低于水位超过 1 小时容差）
        var rollback = NewGate(now: () => signedDay.AddDays(-10),
            waterMark: new MemoryWaterMark { Value = signedDay });
        rollback.Refresh();
        Assert.Equal(LicenseState.Expired, rollback.CurrentState);
        Assert.Contains("回拨", rollback.LastReason);

        // 永久授权回拨只留告警，不锁
        var permanentGate = NewGate(now: () => signedDay);
        permanentGate.Refresh();
        string permanent = MakeCode(LicenseCodec.ProductCureLine, _fp, permanentGate.CurrentChallenge());
        Assert.Null(permanentGate.TryActivate(permanent));
        var permanentRollback = NewGate(now: () => signedDay.AddDays(-10),
            waterMark: new MemoryWaterMark { Value = signedDay });
        permanentRollback.Refresh();
        Assert.Equal(LicenseState.Permanent, permanentRollback.CurrentState);
        Assert.Contains("回拨", permanentRollback.LastReason);
    }

    [Fact(DisplayName = "演示模式 Simulate=1 免激活放行")]
    public void Gate_Simulate_BypassesActivation()
    {
        var gate = NewGate(simulate: () => true, fingerprint: new FixedFingerprint("", ok: false));
        gate.Refresh();
        Assert.True(gate.IsSimulated);
        Assert.Equal(LicenseState.Permanent, gate.CurrentState);
        Assert.True(gate.IsProductionAllowed());
        Assert.Equal("授权：演示", gate.ShortStatusText());
        Assert.Null(gate.TryActivate("anything"));   // 演示模式激活动作无意义
    }

    [Fact(DisplayName = "清空激活记录回到未激活")]
    public void Gate_ClearActivation()
    {
        var gate = NewGate();
        gate.Refresh();
        Assert.Null(gate.TryActivate(MakeCode(LicenseCodec.ProductCureLine, _fp, gate.CurrentChallenge())));
        Assert.Equal(LicenseState.Permanent, gate.CurrentState);

        gate.ClearActivation();
        gate.Refresh();
        Assert.Equal(LicenseState.NotActivated, gate.CurrentState);
        Assert.False(gate.IsProductionAllowed());
    }

    [Fact(DisplayName = "指纹采集失败：从严锁死并给出原因")]
    public void Gate_FingerprintFailure_Blocks()
    {
        var gate = NewGate(fingerprint: new FixedFingerprint("", ok: false));
        gate.Refresh();
        Assert.Equal(LicenseState.FingerprintError, gate.CurrentState);
        Assert.Contains("模拟采集失败", gate.LastReason);
        Assert.False(gate.IsProductionAllowed());
    }

    // ════════════════ 激活窗 VM ════════════════

    [Fact(DisplayName = "激活窗 VM：设备码/状态/错误提示/成功关窗")]
    public void ActivationVm_Flow()
    {
        var gate = NewGate();
        gate.Refresh();
        var vm = new LicenseActivationViewModel(gate, "固化程控");

        Assert.Equal("固化程控", vm.AppName);
        Assert.StartsWith("CURE-", vm.DeviceCode);
        Assert.Contains("未激活", vm.StatusText);
        Assert.True(vm.CloseCommand.CanExecute(null));

        // 空码：不可激活、点下去只提示
        Assert.False(vm.ActivateCommand.CanExecute(null));
        vm.ActivateCommand.Execute(null);
        Assert.Contains("请先粘贴激活码", vm.Tip);

        // 设备码带来的挑战值 + 正确的码
        Assert.True(LicenseCodec.TryParseDeviceCode(vm.DeviceCode, out ushort pid,
            out string fp8, out string ch8));
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;
        vm.LicenseCode = MakeCode(pid, fp8, ch8);

        Assert.True(vm.ActivateCommand.CanExecute(null));
        vm.ActivateCommand.Execute(null);
        Assert.Equal("激活成功", vm.Tip);
        Assert.True(closed);
        Assert.Contains("永久使用", vm.StatusText);
        Assert.Equal("", vm.LicenseCode);   // 成功后清空输入框
    }
}
