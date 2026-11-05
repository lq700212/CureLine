// =========================================================================
// LicenseGate：软件授权门禁（编解码在 LicenseCodec，指纹在 MachineFingerprint）
//
// 【职责】把"本机指纹 + ini 里的激活码 + 当前时间"算成五态授权状态，
//   并负责激活落盘、防回拨水位、状态变更通知。全工程判断"能不能生产"只许调
//   本类 IsProductionAllowed()，不要自己读 ini 判断——判定口径只许这一处。
//
// 【五态】NotActivated（无记录/码无效/产品不符/挑战不符）→ Expired（试用
//   到期或时钟回拨）→ Trial / Permanent（可用）→ FingerprintError（ini 记录
//   与本机指纹不符：整目录克隆/换硬件）。Refresh() 是唯一重算口。
//
// 【产品隔离】验签通过只证明"码是厂商用同一把私钥签的"；还要再查 payload
//   产品 ID == 本产品（CureLine=0x434C）。HJVision 的码（0xFFFF）在这里被
//   明确拒绝，不会因为共用密钥对而串用。
//
// 【挑战值防重放】激活窗每次打开 NewChallenge() 换随机值，设备码带上它；
//   签回的激活码 payload 里也带同一个挑战值。窗口重开挑战值即变——窃录的
//   旧设备码 + 旧激活码对不上新挑战值，重放无效。
//
// 【防回拨水位】三存储：ini [License]/WaterMark + HKCU 注册表 + SQLite
//   app_meta 表；三处取最大值再与"构建日期/exe 文件时间"取大做下限。
//   本地时间早于水位超过 1 小时容差 = 判定回拨（试用按过期处理，永久留日志）。
//   单点只读/删改挡不住另外两处；三处全删还有 exe 时间戳下限。
//
// 【可测性】本类是普通实例类（不是静态类）：构造可注入 ini 路径、指纹提供器、
//   时钟、验签委托、水位存储、模拟模式判定与构建日期。生产用静态 Default
//   单例。单元测试用 InMemoryWaterMarkStore + 固定指纹 + 可控时钟，不碰真机。
//
// 【线程】Refresh/TryActivate/ClearActivation 只在 UI 线程调（启动、激活窗、
//   每小时巡检）；指纹采集可后台 Warmup() 预热。StateChanged 在调用线程同步
//   触发，UI 订阅方自行封送（本项目调用点都在 UI 线程，无需额外处理）。
// =========================================================================

using System.IO;
using Microsoft.Win32;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Security;

/// <summary>授权五态（顺序是历史/序列化口径，勿调整数字）。</summary>
public enum LicenseState
{
    /// <summary>无激活记录 / 激活码无效（被篡改、非本体系、产品不符）。</summary>
    NotActivated = 0,

    /// <summary>试用授权中（TrialExpiry 前有效）。</summary>
    Trial = 1,

    /// <summary>永久授权。</summary>
    Permanent = 2,

    /// <summary>试用到期 / 时钟回拨被判定。</summary>
    Expired = 3,

    /// <summary>本机指纹与记录不符（目录克隆 / 硬件变化 / ini 被改）。</summary>
    FingerprintError = 4,
}

/// <summary>状态变更事件参数（前态→后态，便于日志/巡检判断边沿）。</summary>
public sealed class LicenseStateChangedEventArgs(LicenseState oldState, LicenseState newState) : EventArgs
{
    public LicenseState OldState { get; } = oldState;
    public LicenseState NewState { get; } = newState;
}

/// <summary>
/// 防回拨水位存储（可测注入点）。
/// Read 返回三存储中的最大值（没有记录返回 DateTime.MinValue）；
/// Write 把水位写进三处（单点失败忽略，另两处仍兜底）。
/// </summary>
public interface ILicenseWaterMarkStore
{
    DateTime Read();
    void Write(DateTime value);
}

/// <summary>
/// 生产水位存储：ini [License]/WaterMark + HKCU\Software\CureLine\License +
/// SQLite app_meta（键 LicenseWaterMark）。
/// </summary>
public sealed class LicenseWaterMarkStore : ILicenseWaterMarkStore
{
    /// <summary>授权记录段名（ini config.ini）。</summary>
    public const string IniSection = "License";

    /// <summary>水位键名（ini 与 app_meta 同名）。</summary>
    public const string WaterMarkKey = "WaterMark";

    /// <summary>SQLite app_meta 水位键。</summary>
    public const string SqliteWaterMarkKey = "LicenseWaterMark";

    /// <summary>注册表水位键（HKCU 用户权限即可写，无需管理员）。</summary>
    public const string RegSubKey = @"Software\CureLine\License";

    private readonly string _iniPath;
    private readonly string _dbPath;

    public LicenseWaterMarkStore(string iniPath, string dbPath)
    {
        _iniPath = iniPath;
        _dbPath = dbPath;
    }

    /// <inheritdoc />
    public DateTime Read()
    {
        DateTime max = DateTime.MinValue;
        // ① ini
        try
        {
            max = Max(max, Parse(IniFile.ReadStr(_iniPath, IniSection, WaterMarkKey, "")));
        }
        catch (Exception)
        {
            // 该存储缺位，另两处兜底
        }
        // ② 注册表
        max = Max(max, Parse(ReadRegistry()));
        // ③ SQLite
        try
        {
            var db = new SqliteHelper(_dbPath);
            max = Max(max, Parse(db.GetMeta(SqliteWaterMarkKey)));
        }
        catch (Exception)
        {
            // 库未建/被锁——不影响另两处
        }
        return max;
    }

    /// <inheritdoc />
    public void Write(DateTime value)
    {
        string text = value.ToString("yyyy-MM-dd HH:mm");
        // ① ini（IniFile 写失败静默，读回校验由 LicenseGate 的激活链负责）
        try { IniFile.Write(_iniPath, IniSection, WaterMarkKey, text); }
        catch (Exception) { }
        // ② 注册表
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(RegSubKey);
            k?.SetValue(WaterMarkKey, text);
        }
        catch (Exception)
        {
            // 权限/只读——单存储失效，另两处仍兜底
        }
        // ③ SQLite
        try
        {
            new SqliteHelper(_dbPath).SetMeta(SqliteWaterMarkKey, text);
        }
        catch (Exception)
        {
            // 库被锁/目录只读——不放大异常
        }
    }

    private static string? ReadRegistry()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RegSubKey);
            return k == null ? null : Convert.ToString(k.GetValue(WaterMarkKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime Parse(string? s) =>
        DateTime.TryParse(s, out DateTime d) ? d : DateTime.MinValue;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>
/// 软件授权门禁实例。生产用 <see cref="Default"/>；测试构造实例注入假依赖。
/// </summary>
public sealed class LicenseGate
{
    // ════════════════ 常量 ════════════════

    /// <summary>授权记录中的指纹键。</summary>
    public const string IniKeyFingerprint = "Fingerprint";

    /// <summary>授权记录中的激活码原文键（每次 Refresh 重新验签，改 ini 只能让授权失效）。</summary>
    public const string IniKeyLicenseCode = "LicenseCode";

    /// <summary>ini 水位键（与 LicenseWaterMarkStore.WaterMarkKey 同源）。</summary>
    public const string IniKeyWaterMark = "WaterMark";

    /// <summary>时钟回拨容差（小时）：给 NTP 校时/时区修正留余量。</summary>
    private const double RollbackToleranceHours = 1.0;

    /// <summary>本项目构建日期（水位下限；exe 文件时间通常更新，两者取大）。</summary>
    public static readonly DateTime BuiltOn = new(2026, 11, 4, 0, 0, 0);

    // ════════════════ 依赖 ════════════════

    private readonly string _iniPath;
    private readonly IFingerprintProvider _fingerprintProvider;
    private readonly Func<DateTime> _now;
    private readonly Func<byte[], byte[], bool> _verify;
    private readonly ILicenseWaterMarkStore _waterMarkStore;
    private readonly Func<bool> _simulate;
    private readonly DateTime _builtOn;
    private readonly Func<DateTime>? _exeTime;
    private readonly ushort _productId;
    private readonly string _productDisplayName;

    private string? _challenge8;
    private bool _refreshing;
    private bool _simulatedActive;

    // ════════════════ 状态 ════════════════

    /// <summary>当前授权状态（Refresh() 重算；初始 NotActivated）。</summary>
    public LicenseState CurrentState { get; private set; } = LicenseState.NotActivated;

    /// <summary>试用到期日（State=Trial 时有意义；到期日当天全天可用）。</summary>
    public DateTime TrialExpiry { get; private set; }

    /// <summary>最近一次 Refresh 判定的补充原因（日志/提示用；可为空）。</summary>
    public string LastReason { get; private set; } = "";

    /// <summary>本授权门禁对应的产品 ID（CureLine 固定 0x434C）。</summary>
    public ushort ProductId => _productId;

    /// <summary>产品展示名（激活窗标题用）。</summary>
    public string ProductDisplayName => _productDisplayName;

    /// <summary>
    /// 当前是否演示模式（Simulate=1，免激活放行）。
    /// 注意：值取自最近一次 Refresh 的快照，不在 getter 里读 ini——避免 UI
    /// 每次绑定求值都碰文件（也保证测试构造 VM 时不产生意外落盘）。
    /// </summary>
    public bool IsSimulated => _simulatedActive;

    /// <summary>状态变更事件（Refresh 结束后触发，含前态→后态）。</summary>
    public event EventHandler<LicenseStateChangedEventArgs>? StateChanged;

    // ════════════════ 构造 ════════════════

    /// <summary>
    /// 可测构造。生产由 <see cref="Default"/> 装配；测试注入固定指纹/时钟/验签/内存水位。
    /// verify 为空时用内置公钥（生产口径）；测试可用临时密钥对验签。
    /// </summary>
    public LicenseGate(
        string iniPath,
        IFingerprintProvider fingerprintProvider,
        Func<DateTime> nowProvider,
        Func<byte[], byte[], bool>? verify = null,
        ILicenseWaterMarkStore? waterMarkStore = null,
        Func<bool>? simulateProvider = null,
        DateTime? builtOn = null,
        Func<DateTime>? exeTimeProvider = null,
        ushort productId = LicenseCodec.ProductCureLine,
        string productDisplayName = "CureLine")
    {
        _iniPath = iniPath;
        _fingerprintProvider = fingerprintProvider;
        _now = nowProvider;
        _verify = verify ?? LicenseCodec.VerifyActivation;
        _waterMarkStore = waterMarkStore
            ?? new LicenseWaterMarkStore(iniPath, AppPaths.DatabaseFile);
        _simulate = simulateProvider ?? (() => false);
        _builtOn = builtOn ?? BuiltOn;
        _exeTime = exeTimeProvider;
        _productId = productId;
        _productDisplayName = productDisplayName;
    }

    // ════════════════ 生产单例 ════════════════

    private static LicenseGate? _default;

    /// <summary>
    /// 生产单例：ini = AppPaths.ConfigIni、指纹 = 真机 WMI、验签 = 内置公钥、
    /// 水位 = 三存储、模拟模式 = config.ini [setting] Simulate。
    /// 首次访问时构建（App.OnStartup 已设好 AppPaths.BaseDir 再访问即可）。
    /// </summary>
    public static LicenseGate Default => _default ??= CreateDefault();

    private static LicenseGate CreateDefault()
    {
        string ini = AppPaths.ConfigIni;
        return new LicenseGate(
            ini,
            new MachineFingerprintProvider(),
            () => DateTime.Now,
            LicenseCodec.VerifyActivation,
            new LicenseWaterMarkStore(ini, AppPaths.DatabaseFile),
            () => string.Equals(
                IniFile.ReadStr(ini, "setting", "Simulate", "0"), "1",
                StringComparison.OrdinalIgnoreCase),
            BuiltOn,
            exeTimeProvider: () =>
            {
                try
                {
                    string? exe = Environment.ProcessPath;
                    return exe != null && File.Exists(exe)
                        ? File.GetLastWriteTime(exe)
                        : DateTime.MinValue;
                }
                catch (Exception)
                {
                    return DateTime.MinValue;
                }
            },
            LicenseCodec.ProductCureLine,
            "CureLine");
    }

    // ════════════════ 指纹 ════════════════

    /// <summary>
    /// 后台预热指纹（启动时 Task.Run 调一次即可，避免激活窗打开时等 WMI）。
    /// 重复调用幂等（提供者内部缓存）。
    /// </summary>
    public void Warmup()
    {
        try { _fingerprintProvider.Get(); }
        catch (Exception) { /* 预热失败不致命，真正取指纹时会同步重查并报原因 */ }
    }

    /// <summary>取本机指纹（8 位大写 hex）；失败返回 false 并带中文原因。</summary>
    public bool TryGetFingerprint(out string fp8Hex, out string error)
    {
        FingerprintResult r = _fingerprintProvider.Get();
        fp8Hex = r.Fingerprint8;
        error = r.Error;
        return r.Ok;
    }

    // ════════════════ 挑战值（防重放） ════════════════

    /// <summary>
    /// 生成新挑战值（激活窗每次打开调一次；旧设备码/旧激活码随之失效）。
    /// 用密码学随机源而非 Random：挑战值参与安全判定，不能可预测。
    /// </summary>
    public string NewChallenge()
    {
        byte[] b = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        _challenge8 = LicenseCodec.BytesToHex(b);
        return _challenge8;
    }

    /// <summary>当前挑战值（无则先造一个——防御调用方直接读的场景）。</summary>
    public string CurrentChallenge() => _challenge8 ?? NewChallenge();

    /// <summary>
    /// 生成激活窗展示的设备码。返回 false = 指纹采集失败（error 带原因），
    /// 激活窗必须显示错误而不是空设备码。
    /// </summary>
    public bool TryBuildDeviceCode(out string deviceCode, out string error)
    {
        deviceCode = "";
        if (!TryGetFingerprint(out string fp8, out error))
            return false;
        deviceCode = LicenseCodec.BuildDeviceCode(_productId, fp8, CurrentChallenge());
        return true;
    }

    // ════════════════ 状态判定（唯一重算口） ════════════════

    /// <summary>
    /// 重算授权状态（启动 / 每小时巡检 / 激活成功后调）。
    /// 判定链：模拟模式 → 指纹 → ini 记录 → 验签 → 产品 → 指纹比对 → 到期日 → 回拨。
    /// 任何一步失败都落在"更严"的状态上（宁可误锁不可漏放）。
    /// </summary>
    public void Refresh()
    {
        // 防重入：StateChanged 订阅方若回调 Refresh 会死循环（本项目无此用法，
        // 但公共 API 留个闸，代价为零）。
        if (_refreshing) return;
        _refreshing = true;
        LicenseState old = CurrentState;
        LastReason = "";
        try
        {
            // 0) 演示模式免硬件免授权（与程序①演示总线同口径；Mock 不影响生产判定）
            _simulatedActive = _simulate();
            if (_simulatedActive)
            {
                CurrentState = LicenseState.Permanent;
                LastReason = "演示模式（Simulate=1）免激活";
                return;
            }

            // 1) 本机指纹（拿不到=无法判定，从严锁死并给原因）
            if (!TryGetFingerprint(out string fp8, out string fpErr))
            {
                CurrentState = LicenseState.FingerprintError;
                LastReason = fpErr;
                return;
            }

            // 2) ini 授权记录
            string storedFp = (IniFile.ReadStr(_iniPath, LicenseWaterMarkStore.IniSection,
                IniKeyFingerprint, "") ?? "").Trim();
            string storedCode = (IniFile.ReadStr(_iniPath, LicenseWaterMarkStore.IniSection,
                IniKeyLicenseCode, "") ?? "").Trim();
            if (storedCode.Length == 0)
            {
                CurrentState = LicenseState.NotActivated;
                LastReason = "无激活记录";
                return;
            }
            if (storedFp != fp8)
            {
                CurrentState = LicenseState.FingerprintError;
                LastReason = "激活记录与本机指纹不符（目录克隆或硬件变化）";
                return;
            }

            // 3) 解码 + 验签（每次 Refresh 都重验原文——篡改 ini 无法延长授权）
            if (!LicenseCodec.TryDecodeActivationCode(storedCode, out byte[] payload, out byte[] sig)
                || !_verify(payload, sig))
            {
                CurrentState = LicenseState.NotActivated;
                LastReason = "激活码无效（验签不通过）";
                return;
            }
            if (!LicenseCodec.TryParsePayload(payload, out ushort productId,
                    out byte[] fp4, out _, out byte type, out byte days, out uint startDays))
            {
                CurrentState = LicenseState.NotActivated;
                LastReason = "激活码 payload 非法";
                return;
            }

            // 4) 产品隔离（同一把私钥签的两个产品，靠 payload 产品 ID 区分）
            if (productId != _productId)
            {
                CurrentState = LicenseState.NotActivated;
                LastReason = $"该激活码属于 {LicenseCodec.GetProductName(productId)}，"
                    + $"不能用于 {_productDisplayName}";
                return;
            }

            // 5) 指纹比对（验签只证明"码是厂商签的"，还要证明"签的是这台机器"）
            byte[] myFp4 = LicenseCodec.HexToBytes(fp8[..8]) ?? Array.Empty<byte>();
            if (!BytesEqual(fp4, myFp4))
            {
                CurrentState = LicenseState.FingerprintError;
                LastReason = "激活码签发指纹与本机不符";
                return;
            }

            // 6) 类型分支
            if (type == LicenseCodec.TypePermanent)
            {
                CurrentState = LicenseState.Permanent;
            }
            else
            {
                if (days < 1 || days > 255)
                {
                    CurrentState = LicenseState.NotActivated;
                    LastReason = "试用天数非法";
                    return;
                }
                DateTime expiry = LicenseCodec.DaysToDate(startDays).AddDays(days);
                if (_now().Date > expiry.Date)
                {
                    CurrentState = LicenseState.Expired;
                    LastReason = "试用已于 " + expiry.ToString("yyyy-MM-dd") + " 到期";
                }
                else
                {
                    CurrentState = LicenseState.Trial;
                    TrialExpiry = expiry;
                }
            }

            // 7) 防回拨水位（试用/永久都推进水位；回拨只对试用有绕过价值，
            //    永久也做检测，留 LastReason 告警痕迹，但不改状态）
            bool rolledBack = UpdateWaterMark();
            if (rolledBack && CurrentState == LicenseState.Trial)
            {
                CurrentState = LicenseState.Expired;
                LastReason = "检测到系统时钟回拨，试用授权已按过期处理";
            }
            else if (rolledBack)
            {
                LastReason = "检测到系统时钟回拨（永久授权不受影响）";
            }
        }
        catch (Exception ex)
        {
            // 判定链任何异常都从严按未激活（绝不异常放行）
            CurrentState = LicenseState.NotActivated;
            LastReason = "授权判定异常: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
#if DEBUG
            // 仅 Debug 构建：真实判定坏态时给生产单例注入 90 天开发授权
            //（口径与方法注释见下方"开发自动授权"区段；Release 客户版整段剔除）
            ApplyDevLicenseIfUnlicensed();
#endif
            // 只有状态真的变了才触发事件（避免每小时巡检无谓刷新 UI）
            //（开发授权注入也并入此边沿——顶栏授权文案随事件即时刷新）
            if (old != CurrentState)
                StateChanged?.Invoke(this, new LicenseStateChangedEventArgs(old, CurrentState));
        }
    }

#if DEBUG
    // ════════════════ 开发自动授权（仅 Debug 构建存在） ════════════════
    //
    // 【为什么】开发机反复编译/装机验证功能时，未激活会每次弹激活窗逼走真码
    //   流程，太繁琐。Debug 构建给生产单例注入 90 天内存态试用授权，免激活。
    //
    // 【安全边界】本区段由 #if DEBUG 编译——Release（客户发布版）整个不存在
    //   于二进制，反编译也找不到，不构成任何破解面。因此发布成品必须 Release
    //   编译（-c Release），Debug exe 只在本机开发/验证用、禁止外发。
    //
    // 【口径】
    //   ① 只认生产单例 Default（ReferenceEquals）——可测实例（单测注入假指纹/
    //     时钟）与 Release 行为完全一致，测试口径零分叉；
    //   ② 真实判定已有效（Trial/Permanent）或演示模式（IsSimulated）时不动
    //     ——真码链路（含换码切换/到期退出）随时可设下面环境变量回归验证；
    //   ③ 真实判定坏态（未激活/过期/指纹不符）才覆盖：注入 Trial 内存态，
    //     不写 ini/注册表/SQLite——防回拨三存储基线零污染；
    //   ④ 每次 Refresh（含每小时巡检）以"今天+90 天"重算，开发场景永不过期；
    //     名义仍是 90 天上限——万一 Debug exe 外流也不构成永久放行（自动兜底）。
    // 【关闭】想走真实激活链（手测激活窗/换码/过期流程）时：设环境变量
    //   CURELINE_DEVLICENSE=off（0/false 同义）再启动即恢复真实判定；该开关
    //   同样只存在于 Debug 二进制，Release 无此逻辑。
    // =========================================================================

    /// <summary>开发自动授权天数（仅编译进 Debug 二进制；Release 无此常量）。</summary>
    private const int DevLicenseDays = 90;

    /// <summary>
    /// （仅 Debug 构建，<see cref="Refresh"/> 尾部调用）生产单例真实判定坏态
    /// 时改写为 90 天内存态试用授权——开发编译免激活。详释见上方区段注释。
    /// </summary>
    private void ApplyDevLicenseIfUnlicensed()
    {
        // 逃生口：环境变量关闭（仅 Debug 二进制存在，客户版无此逻辑）
        string? flag = Environment.GetEnvironmentVariable("CURELINE_DEVLICENSE");
        bool suppressed = flag is not null && (
            flag.Equals("off", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("false", StringComparison.OrdinalIgnoreCase)
            || flag == "0");
        if (suppressed)
            return;

        // 只对生产单例生效——单测/手动构造实例（假依赖）绝不踏入保状态
        if (!ReferenceEquals(this, Default))
            return;
        // 真实判定已有效或演示模式：保持真实语义，不覆盖
        if (CurrentState is LicenseState.Permanent or LicenseState.Trial)
            return;
        if (IsSimulated)
            return;

        CurrentState = LicenseState.Trial;
        TrialExpiry = _now().Date.AddDays(DevLicenseDays);
        LastReason = $"开发构建自动授权（{DevLicenseDays} 天试用，仅 Debug 版存在）";
    }
#endif

    /// <summary>
    /// 生产放行判据（全工程唯一口；演示模式恒 true）。
    /// 试用中/永久=true；未激活/过期/指纹异常一律 false。
    /// </summary>
    public bool IsProductionAllowed()
    {
        if (IsSimulated) return true;
        return CurrentState is LicenseState.Permanent or LicenseState.Trial;
    }

    /// <summary>当前是否已授权（Trial/Permanent；演示模式也算——UI 状态展示用）。</summary>
    public bool IsActivated => IsProductionAllowed();

    /// <summary>激活窗状态行文案（含剩余天数）。</summary>
    public string StateText()
    {
        if (IsSimulated) return "激活状态：演示模式（免激活）";
        return CurrentState switch
        {
            LicenseState.Permanent => "激活状态：永久使用",
            LicenseState.Trial => "激活状态：试用中（剩余 "
                + Math.Max(0, (TrialExpiry.Date - _now().Date).Days) + " 天，到期 "
                + TrialExpiry.ToString("yyyy-MM-dd") + "）",
            LicenseState.Expired => "激活状态：软件已过期",
            LicenseState.FingerprintError => "激活状态：设备指纹不符，请联系厂商",
            _ => "激活状态：未激活",
        };
    }

    /// <summary>
    /// 顶栏短文案（空间小，只抓重点）：永久 / 试用剩 N 天 / 未激活 / 已过期 / 指纹不符 / 演示。
    /// </summary>
    public string ShortStatusText()
    {
        if (IsSimulated) return "授权：演示";
        return CurrentState switch
        {
            LicenseState.Permanent => "授权：永久",
            LicenseState.Trial => "授权：试用剩 "
                + Math.Max(0, (TrialExpiry.Date - _now().Date).Days) + " 天",
            LicenseState.Expired => "授权：已过期",
            LicenseState.FingerprintError => "授权：指纹不符",
            _ => "授权：未激活",
        };
    }

    /// <summary>给激活窗的详细原因（失败时展示；成功时为空）。</summary>
    public string DetailText()
    {
        if (!string.IsNullOrEmpty(LastReason)) return LastReason;
        return CurrentState switch
        {
            LicenseState.Permanent => "本机已永久授权。如需更换授权类型（限期↔永久），"
                + "按当前设备码向厂商签发新激活码，粘贴到下方输入框即可切换（立即生效）。",
            LicenseState.Trial => "试用期内可正常使用。可粘贴新激活码更换授权类型"
                + "（限期↔永久均可，立即生效，无需重启）；到期后需输入新激活码。",
            _ => "请把设备码提供给厂商，收到激活码后粘贴到下方输入框。",
        };
    }

    // ════════════════ 激活 ════════════════

    /// <summary>
    /// 尝试激活：验签 → 产品 → 指纹比对 → 挑战值比对（防重放）→ 写 ini → Refresh。
    /// 返回 null=成功；非 null=失败原因（中文，直接展示给用户）。
    /// 挑战值用本实例当前值（激活窗打开时 NewChallenge 生成的那个）——
    /// 收旧码重放在这里被拦下。
    ///
    /// 【换码切换（已激活状态下重激活）】允许覆盖现有授权：已激活（Trial/
    /// Permanent）时再输入一枚合法新码（按窗口当前设备码重新签发）同样走本
    /// 链路完成"限期↔永久"互切——落盘即覆盖旧记录，Refresh 即刻生效，热更
    /// 无需重启、无需先清授权。挑战值防线保持原样：激活窗重开即换新挑战值，
    /// 拿旧设备码签的码在这里被拦——换码必须用窗口当次显示的设备码重新签发。
    /// </summary>
    public string? TryActivate(string codeText)
    {
        if (_simulate())
            return null;    // 演示模式已放行，激活动作无意义（UI 也隐藏入口）

        if (!TryGetFingerprint(out string fp8, out string fpErr))
            return "无法获取本机设备指纹：" + fpErr;

        if (!LicenseCodec.TryDecodeActivationCode(codeText, out byte[] payload, out byte[] sig))
            return "激活码格式不正确，请完整复制厂商返回的激活码";
        if (!_verify(payload, sig))
            return "激活码无效（签名验证不通过），请核对后重新输入";
        if (!LicenseCodec.TryParsePayload(payload, out ushort productId,
                out byte[] fp4, out byte[] ch4, out byte type, out byte days, out _))
            return "激活码内容非法";

        if (productId != _productId)
            return $"该激活码属于 {LicenseCodec.GetProductName(productId)}，"
                + $"不能用于 {_productDisplayName}，请向厂商索取本软件的激活码";

        byte[] myFp4 = LicenseCodec.HexToBytes(fp8[..8]) ?? Array.Empty<byte>();
        if (!BytesEqual(fp4, myFp4))
            return "该激活码不是签给本机的（设备指纹不符），请重新提供设备码";

        // 防重放：payload 挑战值必须等于当前会话挑战值。窗口重开即换新，
        // 之前窃录的设备码+激活码在这里全部失效。
        string challenge = CurrentChallenge();
        if (!string.Equals(LicenseCodec.BytesToHex(ch4), challenge,
                StringComparison.OrdinalIgnoreCase))
            return "激活码已失效（设备码已刷新），请重新生成设备码发送厂商";

        if (type == LicenseCodec.TypeTrial && (days < 1 || days > 255))
            return "激活码试用天数非法";

        // 全部通过：落盘。LicenseCode 存原文（每次 Refresh 重新验签），
        // Fingerprint 锁本机。
        IniFile.Write(_iniPath, LicenseWaterMarkStore.IniSection, IniKeyFingerprint, fp8);
        IniFile.Write(_iniPath, LicenseWaterMarkStore.IniSection, IniKeyLicenseCode, codeText.Trim());

        // 写后回读自检（ini 只读时 IniFile 静默失败——这里让它现形）
        string back = (IniFile.ReadStr(_iniPath, LicenseWaterMarkStore.IniSection,
            IniKeyLicenseCode, "") ?? "").Trim();
        if (!string.Equals(back, codeText.Trim(), StringComparison.Ordinal))
            return "激活码写入失败（配置文件可能只读），请检查 config.ini 权限后重试";

        Refresh();
        if (CurrentState is not (LicenseState.Permanent or LicenseState.Trial))
            return "激活码写入后校验未通过" +
                (string.IsNullOrEmpty(LastReason) ? "（存储异常）" : "：" + LastReason);
        return null;
    }

    /// <summary>清空授权记录（设备指纹不符时的自愈入口——重新激活即可覆盖）。</summary>
    public void ClearActivation()
    {
        try
        {
            IniFile.Write(_iniPath, LicenseWaterMarkStore.IniSection, IniKeyFingerprint, "");
            IniFile.Write(_iniPath, LicenseWaterMarkStore.IniSection, IniKeyLicenseCode, "");
        }
        catch (Exception)
        {
            // 清记录失败下次 Refresh 会按指纹不符锁死，不致命（可手工删 ini 段）
        }
    }

    // ════════════════ 防回拨水位 ════════════════

    /// <summary>
    /// 推进防回拨水位。返回 true=检测到时钟回拨。
    /// 下限 = max(构建日期, exe 文件时间)；三存储现值取最大；当前时间早于
    /// 水位超过容差即判定回拨。正常时把 max(now, 水位) 写回三处。
    /// </summary>
    private bool UpdateWaterMark()
    {
        try
        {
            DateTime now = _now();
            DateTime hwm = _builtOn;
            try
            {
                if (_exeTime != null) hwm = Max(hwm, _exeTime());
            }
            catch (Exception)
            {
                // exe 时间读不到——还有构建日期与三存储兜底
            }
            hwm = Max(hwm, _waterMarkStore.Read());

            // 回拨判定：当前时间早于水位超过容差
            if (now < hwm.AddHours(-RollbackToleranceHours))
                return true;

            // 正常推进：水位 = max(now, hwm) 写三处
            _waterMarkStore.Write(now > hwm ? now : hwm);
            return false;
        }
        catch (Exception)
        {
            // 水位链路异常不阻塞主判定（状态已算完），返回 false 不误报回拨
            return false;
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
