// =========================================================================
// 软件授权测试（V0.0.62 起真源 = kaleidoscope 子模块 Kaleidoscope.Security）
//
// 覆盖：双产品设备码/校验位/手抄容错、Base32 往返、payload 产品 ID 布局、
//   内置公钥防篡改、era 挑战值（档内恒定+设备码内嵌一致）、门禁（无记录/
//   记录被篡改/指纹克隆/清空自愈/演示桥放行）、激活窗 VM 编排。
//
// 【测试模式差异（对 V0.0.61 及之前）】子模块 LicenseGate 是静态类（宿主侧
//   无假指纹/假时钟/临时密钥注入点），本组用例改为：
//   · 状态文件笔沙箱：LicenseGate.StateIniPathOverride → %TEMP% 随笔目录；
//   · 注册表水位沙箱：LicenseGate.RegRootOverride → HKCU\Software\CureLine\
//     TestLicenseWaterMark（用后删键，不碰真机水位）；
//   · 开发兜底关闭：env LicenseGate.EnvKeyDebugLicenseOff="0"（仅 Debug 库存在，
//     与子模块回归探针同款——保真"无记录=NotActivated"等真实态断言）；
//   · 演示桥直注：LicenseBridges.IsMockLicensedProvider 按用例覆盖并还原。
//   依赖注入类语义（试用到期/时钟回拨/换码成功链/产品隔离激活链——需要假时钟
//   与厂商私钥签真码）不在此测：真源逻辑锁在 kaleidoscope 的回归探针批
//   (.opencode/skill/acceptance-test) 里，宿主侧只测接线真子集。
//
//   编解码/设备码用例全部走 Kaleidoscope.Security.LicenseCodec 纯函数（真源
//   同名直迁）；自签签名仅用于"防篡改验签必拒"（内置公钥验自签码必失败）。
// =========================================================================

using System.Security.Cryptography;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.ViewModels;
using Kaleidoscope.Security;
using LicenseState = Kaleidoscope.Security.LicenseState;

namespace CureLine.Tests;

public class LicenseTests : IDisposable
{
    private readonly string _tmp;
    private readonly ECDsa _signer;
    private const string _fp = "11223344";
    private const string _ch = "AABBCCDD";
    private readonly Func<bool>? _prevMockProvider;
    private const string TestRegRoot = @"Software\CureLine\TestLicenseWaterMark";
    private const string HostRegRoot = @"Software\CureLine\License";   // LicenseHost.Setup 口径（还原用）
    private const string HostStateIni = @"MainSetting\LicenseState.ini"; // 子模块缺省口径（还原用）

    public LicenseTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "CureLineLic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // —— 沙箱三件：状态文件 / 注册表水位键 / 关闭 Debug 开发兜底（真实态断言）——
        Environment.SetEnvironmentVariable(LicenseGate.EnvKeyDebugLicenseOff, "0");
        LicenseGate.StateIniPathOverride = Path.Combine(_tmp, "LicenseState.ini");
        LicenseGate.RegRootOverride = TestRegRoot;

        // —— 覆盖点注入（幂等）+ 演示桥缺省还原为"非演示"（演示放行在专用用例里单独开）——
        LicenseHost.Setup();
        _prevMockProvider = LicenseBridges.IsMockLicensedProvider;
        LicenseBridges.IsMockLicensedProvider = null;
    }

    public void Dispose()
    {
        _signer.Dispose();
        LicenseBridges.IsMockLicensedProvider = _prevMockProvider;
        LicenseGate.RegRootOverride = HostRegRoot;
        LicenseGate.StateIniPathOverride = null;   // 还原子模块缺省 exe\MainSetting 口径
        Environment.SetEnvironmentVariable(LicenseGate.EnvKeyDebugLicenseOff, null);
        CleanupTestRegistryKey();
        try { Directory.Delete(_tmp, true); } catch { }
    }

    /// <summary>删除沙箱注册表水位键（父键 CureLine 可能还会被真机授权用——只删测试键）。</summary>
    private void CleanupTestRegistryKey()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? parent =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\CureLine", writable: true);
            parent?.DeleteSubKeyTree("TestLicenseWaterMark", throwOnMissingSubKey: false);
        }
        catch
        {
            // 原因：清理失败不掩盖测试结论（键残留无害，同名键下次被覆盖）
        }
    }

    // ════════════════ 测试夹具 ════════════════

    /// <summary>
    /// 用临时私钥自签一个激活码（仅"防篡改/假码被拒"用例用——必须显式传 signer；
    /// 门禁激活成功链不在此测：真码依赖厂商私钥，语义锁在 kaleidoscope 回归探针）。
    /// </summary>
    private string MakeCode(ushort productId, string fp8, string ch8,
        byte type = LicenseCodec.TypePermanent, byte days = 0, DateTime? start = null,
        ECDsa? signer = null)
    {
        signer ??= _signer;
        byte[] payload = LicenseCodec.BuildPayload(
            productId, LicenseCodec.HexToBytes(fp8)!, LicenseCodec.HexToBytes(ch8)!,
            type, days, LicenseCodec.DateToDays(start ?? DateTime.Now));
        return LicenseCodec.EncodeActivationCode(payload, signer.SignData(payload, HashAlgorithmName.SHA256));
    }

    /// <summary>把授权记录写进沙箱 LicenseState.ini（门禁链用例的记录打底）。</summary>
    private void WriteRecord(string fingerprint, string licenseCode)
    {
        var ini = new LicenseStateIni(LicenseGate.StateIniPathOverride!);
        ini.WriteValue("License", "Fingerprint", fingerprint);
        ini.WriteValue("License", "LicenseCode", licenseCode);
    }

    // ════════════════ 编解码 / 设备码 ════════════════

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

    [Fact(DisplayName = "payload 产品 ID 大端写入，解析回读一致")]
    public void Payload_ProductId_BigEndian()
    {
        byte[] p = LicenseCodec.BuildPayload(LicenseCodec.ProductCureLine,
            LicenseCodec.HexToBytes(_fp), LicenseCodec.HexToBytes(_ch),
            LicenseCodec.TypePermanent, 0, 0);
        Assert.Equal(0x43, p[14]);
        Assert.Equal(0x4C, p[15]);
        Assert.True(LicenseCodec.TryParsePayload(p, out ushort pid, out _, out _, out _, out _, out _));
        Assert.Equal(LicenseCodec.ProductCureLine, pid);
    }

    [Fact(DisplayName = "篡改 payload 后内置公钥验签必失败（防离线伪造）")]
    public void ActivationCodec_Tamper_Rejected()
    {
        string code = MakeCode(LicenseCodec.ProductCureLine, _fp, _ch, signer: _signer);
        Assert.True(LicenseCodec.TryDecodeActivationCode(code, out byte[] payload, out byte[] sig));
        // 自签的码能通过签名长度/格式校验，但内置真公钥验签必拒（信任根只有厂商私钥）
        Assert.False(LicenseCodec.VerifyActivation(payload, sig));

        // 换真 payload 字段同样被拦（占位签名的伪造路径全军覆没）
        payload[9] = 99;                       // 改试用天数
        Assert.False(LicenseCodec.VerifyActivation(payload, sig));
    }

    // ════════════════ era 挑战值（设备码内嵌一致 + 档内恒定） ════════════════

    [Fact(DisplayName = "era 挑战值档内恒定，与设备码内嵌挑战一致")]
    public void EraChallenge_StableAndEmbeddedInDeviceCode()
    {
        string c1 = LicenseGate.NewChallenge();
        string c2 = LicenseGate.NewChallenge();   // 同 30 天档内两次派生必然同值
        Assert.Equal(8, c1.Length);
        Assert.Equal(c1, c2);

        Assert.True(LicenseHost.TryBuildDeviceCode(out string device, out _));
        Assert.True(LicenseCodec.TryParseDeviceCode(device, out _, out string _, out string ch));
        Assert.Equal(c1, ch);   // 设备码内嵌的挑战 = 当前 era 挑战（激活校验同源）
    }

    // ════════════════ 门禁（沙箱状态文件 + 真机指纹） ════════════════

    private LicenseState RefreshState()
    {
        LicenseHost.Refresh();
        return LicenseHost.CurrentState;
    }

    [Fact(DisplayName = "无记录=未激活且不放行")]
    public void Gate_NoRecord_NotActivated()
    {
        Assert.Equal(LicenseState.NotActivated, RefreshState());
        Assert.False(LicenseHost.IsProductionAllowed);
        Assert.Equal("授权：未激活", LicenseHost.ShortStatusText());
        Assert.Equal("激活状态：未激活", LicenseHost.StateText());
    }

    [Fact(DisplayName = "演示桥（Mock=放行）免激活直接放行")]
    public void Gate_MockBridge_Bypasses()
    {
        LicenseBridges.IsMockLicensedProvider = () => true;
        Assert.Equal(LicenseState.Permanent, RefreshState());
        Assert.True(LicenseHost.IsSimulated);
        Assert.True(LicenseHost.IsProductionAllowed);
        Assert.Equal("授权：演示", LicenseHost.ShortStatusText());
        Assert.Equal("激活状态：演示模式（免激活）", LicenseHost.StateText());
        Assert.Null(LicenseHost.TryActivate("anything"));   // 演示模式激活动作无意义
    }

    [Fact(DisplayName = "记录被篡改（验签不过）→ 从严回到未激活")]
    public void Gate_TamperedRecord_NotActivated()
    {
        // 指纹写真机（否则先撞"记录指纹不符"测不到验签环节）；码放垃圾串
        Assert.True(LicenseGate.TryGetFingerprint(out string fp8, out string _));
        WriteRecord(fp8, "GARBAGE_NOT_A_CODE");
        Assert.Equal(LicenseState.NotActivated, RefreshState());
        Assert.False(LicenseHost.IsProductionAllowed);
    }

    [Fact(DisplayName = "记录指纹与本机不符（目录克隆）→ 锁死 FingerprintError")]
    public void Gate_CloneFingerprint_FingerprintError()
    {
        WriteRecord("DEADBEEF", "SOME-CODE-GOES-HERE");
        Assert.Equal(LicenseState.FingerprintError, RefreshState());
        Assert.False(LicenseHost.IsProductionAllowed);
        Assert.Equal("授权：指纹不符", LicenseHost.ShortStatusText());
        Assert.Contains("指纹不符", LicenseHost.LastReason);
    }

    [Fact(DisplayName = "清空激活记录回到未激活（自愈入口）")]
    public void Gate_ClearActivation()
    {
        WriteRecord("DEADBEEF", "SOME-CODE-GOES-HERE");
        Assert.Equal(LicenseState.FingerprintError, RefreshState());
        LicenseHost.ClearActivation();
        Assert.Equal(LicenseState.NotActivated, RefreshState());
        Assert.False(LicenseHost.IsProductionAllowed);
    }

    // ════════════════ 真私钥激活链（厂商私钥随库的环境条件用例） ════════════════

    /// <summary>
    /// 厂商真私钥（tools\LicenseIssuer\LicenseSignKey.dat，不入仓库）。缺失时
    /// 激活成功链用例退化为静默通过（用例结束）——开发机（有私钥）才会真正跑
    /// 语义断言；无钥环境由 kaleidoscope 回归探针回归同语义。激活成功链必须
    /// 真码：真源验签公钥固定（LicenseCodec.PubKeyB64），无法注入临时密钥对。
    /// </summary>
    private string? VendorSigner()
    {
        // tests bin → 仓库根 → tools\LicenseIssuer\LicenseSignKey.dat
        string? dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "tools", "LicenseIssuer", "LicenseSignKey.dat");
            if (File.Exists(candidate))
            {
                string b64 = File.ReadAllText(candidate).Trim();
                return string.IsNullOrEmpty(b64) ? null : b64;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>把 SQLite 水位桥接到沙箱库（真跑三存储第三路；Dispose 里还原 null=缺位语义）。</summary>
    private void UseSqliteBridgeInTemp()
    {
        string db = Path.Combine(_tmp, "LicenseWaterMark.db3");
        new SqliteHelper(db).EnsureDatabase();
        LicenseBridges.SqliteWaterMarkGet = key => new SqliteHelper(db).GetMeta(key);
        LicenseBridges.SqliteWaterMarkSet = (key, v) => new SqliteHelper(db).SetMeta(key, v);
    }

    /// <summary>
    /// 出本机真设备码并回读内嵌指纹/挑战（激活链用例的 payload 真值源）。
    /// 真机必须出 CURE 前缀码——设备码产品归属即本产品（前缀↔产品 ID 同源校验）。
    /// </summary>
    private (string Fp8, string Ch8) LocalDeviceParts()
    {
        Assert.True(LicenseHost.TryBuildDeviceCode(out string device, out string fpErr),
            "激活链用例必须能出设备码（真机 MachineGuid）：" + fpErr);
        Assert.True(LicenseCodec.TryParseDeviceCode(device, out ushort deviceProduct,
            out string fp8, out string ch8));
        Assert.Equal(LicenseCodec.ProductCureLine, deviceProduct);
        return (fp8, ch8);
    }

    /// <summary>按厂商私钥签真码（ECDsaCng CNG blob 口径与签发工具 SignCore 一致）。</summary>
    private string VendorSignCode(string keyB64, ushort productId,
        string fp8, string ch8, byte type, byte days)
    {
        byte[] payload = LicenseCodec.BuildPayload(productId,
            LicenseCodec.HexToBytes(fp8)!, LicenseCodec.HexToBytes(ch8)!,
            type, days, LicenseCodec.DateToDays(DateTime.Now));
        using CngKey k = CngKey.Import(Convert.FromBase64String(keyB64), CngKeyBlobFormat.EccPrivateBlob);
        using ECDsaCng signer = new(k);
        signer.HashAlgorithm = CngAlgorithm.Sha256;
        return LicenseCodec.EncodeActivationCode(payload, signer.SignData(payload));
    }

    [Fact(DisplayName = "厂商真码激活：永久落盘，重启恢复依旧放行（私钥缺失则静默跳过）")]
    public void Activate_WithVendorKey_PermanentPersists()
    {
        string? key = VendorSigner();
        if (key == null) return;   // 无厂商私钥环境：语义锁在 kaleidoscope 探针，本用例跳过断言
        UseSqliteBridgeInTemp();
        Assert.Equal(LicenseState.NotActivated, RefreshState());

        (string fp8, string ch8) = LocalDeviceParts();
        string? code = VendorSignCode(key, LicenseCodec.ProductCureLine, fp8, ch8,
            LicenseCodec.TypePermanent, 0);
        Assert.NotNull(code);
        Assert.Null(LicenseHost.TryActivate(code));   // 激活成功
        Assert.Equal(LicenseState.Permanent, LicenseHost.CurrentState);
        Assert.True(LicenseHost.IsProductionAllowed);
        Assert.Equal("授权：永久", LicenseHost.ShortStatusText());

        // 模拟重启：Refresh 重读沙箱记录 → 依旧 Permanent（落盘恢复语义）
        Assert.Equal(LicenseState.Permanent, RefreshState());
    }

    [Fact(DisplayName = "厂商真码换码切换：永久→限期立即生效（私钥缺失则静默跳过）")]
    public void Reactivate_WithVendorKey_PermanentToTrial()
    {
        string? key = VendorSigner();
        if (key == null) return;
        UseSqliteBridgeInTemp();
        Assert.Equal(LicenseState.NotActivated, RefreshState());

        (string fp8, string ch8) = LocalDeviceParts();
        string? permanent = VendorSignCode(key, LicenseCodec.ProductCureLine, fp8, ch8,
            LicenseCodec.TypePermanent, 0);
        Assert.NotNull(permanent);
        Assert.Null(LicenseHost.TryActivate(permanent));
        Assert.Equal(LicenseState.Permanent, LicenseHost.CurrentState);

        // 已激活状态输 30 天试用期真码（era 档内挑战同源）——覆盖落盘，Refresh 即 Trial
        string? trial = VendorSignCode(key, LicenseCodec.ProductCureLine, fp8, ch8,
            LicenseCodec.TypeTrial, 30);
        Assert.NotNull(trial);
        Assert.Null(LicenseHost.TryActivate(trial));
        Assert.Equal(LicenseState.Trial, LicenseHost.CurrentState);
        Assert.True(LicenseHost.IsProductionAllowed);
        Assert.Equal("授权：试用剩 30 天", LicenseHost.ShortStatusText());
    }

    [Fact(DisplayName = "产品隔离激活链：HJVision 真签名码必须被拒（私钥缺失则静默跳过）")]
    public void Activate_WithVendorKey_WrongProductRejected()
    {
        string? key = VendorSigner();
        if (key == null) return;
        UseSqliteBridgeInTemp();
        Assert.Equal(LicenseState.NotActivated, RefreshState());

        // 本机真指纹/真挑战签 HJVision 产品码——校验链产品隔离拦在最前，
        // 被拒的唯一可能原因就是产品 ID 不符（真源拦截失效时码会真激活，测试照样红）。
        (string fp8, string ch8) = LocalDeviceParts();
        string? hjCode = VendorSignCode(key, LicenseCodec.ProductHuaJiVision, fp8, ch8,
            LicenseCodec.TypePermanent, 0);
        Assert.NotNull(hjCode);
        string? err = LicenseHost.TryActivate(hjCode);
        Assert.NotNull(err);                                   // 被拒
        Assert.Contains("HJVision", err);                      // 提示码属于别家产品
        Assert.Equal(LicenseState.NotActivated, LicenseHost.CurrentState);   // 未激活且不放行
        Assert.False(LicenseHost.IsProductionAllowed);
    }

    [Fact(DisplayName = "激活窗 VM：设备码/状态行/空码提示/假码被拒")]
    public void ActivationVm_Flow()
    {
        Assert.Equal(LicenseState.NotActivated, RefreshState());   // 打座：无记录
        var vm = new LicenseActivationViewModel("固化程控");

        Assert.Equal("固化程控", vm.AppName);
        // 指纹采集失败的机器（极端异常环境）设备码为空并给红色提示——真机正常路径
        Assert.True(LicenseGate.TryGetFingerprint(out string _, out string _),
            "真机必须能采集指纹（MachineGuid 主锚点）");
        Assert.StartsWith("CURE-", vm.DeviceCode);
        Assert.Contains("未激活", vm.StatusText);
        Assert.True(vm.CloseCommand.CanExecute(null));

        // 空码：不可激活、点下去只提示
        Assert.False(vm.ActivateCommand.CanExecute(null));
        vm.ActivateCommand.Execute(null);
        Assert.Contains("请先粘贴激活码", vm.Tip);

        // 假码（自签占位签名）：验签/解码失败路径 → 红字提示、不关窗
        Assert.True(LicenseCodec.TryParseDeviceCode(vm.DeviceCode, out ushort pid,
            out string fp8, out string ch8));
        Assert.Equal(LicenseCodec.ProductCureLine, pid);   // 产品隔离：设备码是本产品前缀
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;
        vm.LicenseCode = MakeCode(pid, fp8, ch8, signer: _signer);

        Assert.True(vm.ActivateCommand.CanExecute(null));
        vm.ActivateCommand.Execute(null);
        Assert.Null(closed);   // 失败不关窗
        Assert.NotEqual("激活成功", vm.Tip);
    }
}
