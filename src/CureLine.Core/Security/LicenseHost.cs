// =========================================================================
// LicenseHost：软件授权宿主接线层（CureLine ⇄ Kaleidoscope.Security 唯一接入口）
//
// 干什么：V0.0.62 起授权体系的"真源"整体收口到通讯子模块 Kaleidoscope.Security
//   （LicenseGate 门禁 + LicenseCodec 编解码 + LicenseBridges 桥 + LicenseStateIni
//   落盘，与 HJVision/GYZVision 同一套下沉代码，算法与已签发激活码逐字节兼容）。
//   本类负责两件事：
//     ① 把"宿主才有答案"的覆盖点一次性注入子模块（产品隔离、注册表根、演示桥、
//       SQLite 水位桥）——只有这里允许出现 Kaleidoscope 覆盖点赋值；子模块缺省
//       值是 HuaJiVision 口径，不经 Setup 直接用会把 CureLine 的码判成别家产品；
//     ② 替补子模块没有的宿主侧能力：顶栏短文案/长文案/详情文案（沿用本项目
//       既有 UI 口径，含演示模式措辞）与授权状态"边沿事件"（子模块 Refresh 无
//       事件，宿主在唯一的 Refresh 口包一层差分触发）。
// 怎么改：
//   · 门禁判定/激活/防回拨/指纹/挑战值逻辑一律改 kaleidoscope/
//     Kaleidoscope/Security/LicenseGate.cs（子模块回归探针在它的 acceptance-test
//     技能里），本文件只许加文案/接线，禁止 copy 判定逻辑——两处各抄一份必然漂移；
//   · 两个 App / 两个主窗 VM / 激活窗 VM / 签发工具都经本类 API，谁也不许直接
//     `using Kaleidoscope.Security` 绕过（签发工具例外：它站在厂商端，只用编码）。
//
// 【注入时间线（App.OnStartup）】LicenseHost.Setup() 必须在首次 LicenseHost.Refresh()
//   之前调用——Setup 幂等，首调即固定覆盖点。主程序/激活窗/测试统一走 LicenseHost。
//
// 【落盘口径（V0.0.62 起，与 kaleidoscope 统一）】
//   · 授权记录+水位 ini：exe\MainSetting\LicenseState.ini（子模块缺省路径，
//     不再写 config\config.ini [License]——运行时商业数据与现场配置文件分离，
//     LicenseState.ini 属 .gitignore 排除项；换机/清目录后重新激活一次即可）；
//   · 防回拨水位三存储 = LicenseState.ini [License]/WaterMark
//     + HKCU\Software\CureLine\License（注册表根保持本项目口径）
//     + SQLite app_meta 键 LicenseWaterMark（经桥回到本项目 SqliteHelper 设施，
//     与旧口径同表同键，已推过的水位无缝衔接）；
//   · 下限 = max(BuiltOnOverride=2026-11-04, exe 文件时间)——子模块缺省值即本项目
//     构建日期，无需覆盖。
//
// 【演示模式】config.ini [setting] Simulate=1 → 演示桥短路放行（Refresh 步骤 0），
//   免硬件免激活；激活窗入口 UI 隐藏 + 命令双保险照旧由 UI 层控制。
//
// 【挑战值（era 派生，V4.4.362 沉库而来）】子模块的挑战值不再是"窗口随机"，而是
//   按指纹+30 天档确定性派生：同一档内设备码恒定，"永久↔限时"换码直接粘贴新码
//   即生效（无需重新抄设备码重签）；跨档后旧码自动失配。激活完成后宿主走
//   TryActivate(code, NewChallenge()) 把当前档挑战值传给子模块校验防重放。
//
// 【线程】Setup/Refresh/TryActivate 只在 UI 线程调（与子模块约定一致）；
//   StateChanged 在 Refresh 调用线程触发——本项目调用点全在 UI 线程，订阅方直接
//   赋值即可；StateChanged 只作 UI 文案刷新与操作日志边沿，禁止在订阅方回调
//   里再调 Refresh（本类已做单层防重入）。
// =========================================================================

using System;
using CureLine.Core.Data;
using CureLine.Core.Infrastructure;
using Kaleidoscope.Security;
using LicenseState = Kaleidoscope.Security.LicenseState;

namespace CureLine.Core.Security;

public static class LicenseHost
{
    // ════════════════ 覆盖点注入（幂等） ════════════════

    private static bool _wired;

    /// <summary>
    /// 注入子模块覆盖点与宿主桥（幂等：重复调用零副作用）。
    /// App.OnStartup 最早调用；测试也可在构造收口同款注入后微调（沙箱路径等）。
    /// 必须先于首次 Refresh——覆盖点是静态字段，子模块判定链每次现读，
    /// 但产品 ID 若晚了会把本产品码判成"别家产品"。
    /// </summary>
    public static void Setup()
    {
        if (_wired) return;
        _wired = true;

        // 产品隔离：payload [14..15] 产品 ID 必须等于它（CureLine=0x434C）。
        // 两产品共用一把 ECDsa 私钥，没这个字段的覆盖 = HJVision 的码能激活本软件。
        LicenseGate.HostProductId = LicenseCodec.ProductCureLine;

        // 注册表水位根：保持本项目口径（与 kaleidoscope/HJVision 共机互不踩值）。
        LicenseGate.RegRootOverride = @"Software\CureLine\License";

        // 状态文件走子模块缺省 exe\MainSetting\LicenseState.ini（口径见类头），
        // 故意不设 StateIniPathOverride——缺省即我们想要的。

        // 演示桥：Simulate=1 免激活放行（读 config.ini 现值，不缓存——配置窗
        // 热改 Simulate 后下一拍 Refresh 即生效）。
        LicenseBridges.IsMockLicensedProvider = () =>
            string.Equals(IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0"),
                "1", StringComparison.OrdinalIgnoreCase);

        // SQLite 水位桥：回到本项目 SqliteHelper 设施（app_meta 表、键同名
        // "LicenseWaterMark"，与 V0.0.62 前的同水位物理位置无缝衔接）。
        LicenseBridges.SqliteWaterMarkGet = key => new SqliteHelper(AppPaths.DatabaseFile).GetMeta(key);
        LicenseBridges.SqliteWaterMarkSet = (key, value) => new SqliteHelper(AppPaths.DatabaseFile).SetMeta(key, value);

        // BuiltOnOverride 缺省 "2026-11-04 00:00" = 本项目 BuiltOn 同值，不覆盖。
    }

    // ════════════════ 主门禁转发 ════════════════

    /// <summary>生产放行唯一口（演示模式恒 true；试用/永久=true，其余 false）。</summary>
    public static bool IsProductionAllowed => LicenseGate.IsProductionAllowed();

    /// <summary>演示模式是否放行（Simulate=1 桥；激活窗 UI 隐藏与否也用它）。</summary>
    public static bool IsSimulated => LicenseBridges.IsMockLicensed;

    /// <summary>当前授权状态（Refresh 重算；启动门禁/巡检/VM 文案消费）。</summary>
    public static LicenseState CurrentState => LicenseGate.CurrentState;

    /// <summary>最近一次 Refresh 的补充原因（巡检失效弹窗/激活窗详情展示用）。</summary>
    public static string LastReason => LicenseGate.LastReason;

    /// <summary>试用到期日（State=Trial 时有意义）。</summary>
    public static DateTime TrialExpiry => LicenseGate.TrialExpiry;

    /// <summary>
    /// 重算授权状态（启动门禁/激活成功后/每小时巡检的唯一入口——必须经本类，
    /// 因为宿主侧 StateChanged 边沿在本方法内差分产生；绕过本类直调子模块
    /// Refresh 会导致订阅方漏刷文案）。
    /// </summary>
    public static void Refresh()
    {
        LicenseGate.Refresh();
        PublishStateEdge();
    }

    /// <summary>后台预热指纹（激活窗/Refresh 首调前的可选优化——WMI 采集几百毫秒）。</summary>
    public static void Warmup() => LicenseGate.Warmup();

    /// <summary>清空授权记录（设备指纹不符时的自愈入口——重新激活即可覆盖）。</summary>
    public static void ClearActivation() => LicenseGate.ClearActivation();

    /// <summary>本机 CPU 序列号（激活窗"设备信息"诊断展示；kaleidoscope 同口径实现）。</summary>
    public static string? GetCpuSerialNumber() => LicenseGate.GetCpuSerialNumber();

    // ════════════════ 宿主边沿事件（子模块无事件，宿主差分补） ════════════════

    private static LicenseState? _lastSeen;
    private static bool _publishing;

    /// <summary>
    /// 授权状态边沿（前态→新态）。触发点：Refresh（启动门禁/巡检/激活成功）、
    /// TryActivate 收口——每次状态真的变了才触发，巡检同值不刷（防顶栏抖动）。
    /// 订阅方约定：只刷新 UI 文案 + 记一行操作日志，禁止回调 Refresh（重入）。
    /// </summary>
    public static event Action<LicenseState, LicenseState>? StateChanged;

    /// <summary>差分上报：状态与上次不同才发事件（进程内首次 Refresh 也算边沿）。</summary>
    private static void PublishStateEdge()
    {
        if (_publishing) return;   // 订阅方重入 Refresh 时兜底——不递归
        LicenseState now = LicenseGate.CurrentState;
        if (now == _lastSeen) return;
        LicenseState old = _lastSeen ?? now;
        _lastSeen = now;
        _publishing = true;
        try { StateChanged?.Invoke(old, now); }
        finally { _publishing = false; }
    }

    // ════════════════ 设备码 / 激活（激活窗链） ════════════════

    /// <summary>
    /// 生成激活窗展示的设备码（"CURE-指纹-挑战-校验位"）。返回 false = 指纹采集
    /// 失败（error 带原因）——激活窗必须显示错误而不是空设备码。
    /// 挑战值用当前 era 档（30 天档内恒定——重开窗/换码不重签口径的根据）。
    /// </summary>
    public static bool TryBuildDeviceCode(out string deviceCode, out string error)
    {
        deviceCode = "";
        if (!LicenseGate.TryGetFingerprint(out string fp8, out error))
            return false;
        deviceCode = LicenseCodec.BuildDeviceCode(
            LicenseGate.HostProductId, fp8, LicenseGate.NewChallenge());
        return true;
    }

    /// <summary>
    /// 尝试激活（验签→产品隔离→指纹比对→era 挑战比对→写 ini→Refresh）。
    /// 返回 null=成功；非 null=失败原因（中文，直接展示给激活窗）。
    /// 挑战值取当前档（与激活窗打开时设备码内嵌的挑战值同源；跨档旧码自动拦）。
    /// 换码切换（永久↔限时）同链路覆盖落盘，Refresh 后即刻热生效。
    /// </summary>
    public static string? TryActivate(string codeText)
    {
        if (IsSimulated)
            return null;    // 演示模式已放行，激活动作无意义（UI 也隐藏入口）

        string? error = LicenseGate.TryActivate(codeText, LicenseGate.NewChallenge());
        if (error != null)
            return error;

        // 子模块 TryActivate 内部 Refresh 过但绕过了宿主差分——这里补发边沿
        //（顶栏/日志跟着即时刷新；同值时 PublishStateEdge 自动跳过）。
        PublishStateEdge();
        return null;
    }

    // ════════════════ UI 文案（保留本项目既有口径，勿随手改措辞） ════════════════

    /// <summary>激活窗状态行文案（含剩余天数与到期日）。</summary>
    public static string StateText()
    {
        if (IsSimulated) return "激活状态：演示模式（免激活）";
        return CurrentState switch
        {
            LicenseState.Permanent => "激活状态：永久使用",
            LicenseState.Trial => "激活状态：试用中（剩余 "
                + Math.Max(0, (TrialExpiry.Date - DateTime.Now.Date).Days) + " 天，到期 "
                + TrialExpiry.ToString("yyyy-MM-dd") + "）",
            LicenseState.Expired => "激活状态：软件已过期",
            LicenseState.FingerprintError => "激活状态：设备指纹不符，请联系厂商",
            _ => "激活状态：未激活",
        };
    }

    /// <summary>顶栏短文案（空间小，只抓重点：永久 / 试用剩 N 天 / 未激活 / 已过期 / 指纹不符 / 演示）。</summary>
    public static string ShortStatusText()
    {
        if (IsSimulated) return "授权：演示";
        return CurrentState switch
        {
            LicenseState.Permanent => "授权：永久",
            LicenseState.Trial => "授权：试用剩 "
                + Math.Max(0, (TrialExpiry.Date - DateTime.Now.Date).Days) + " 天",
            LicenseState.Expired => "授权：已过期",
            LicenseState.FingerprintError => "授权：指纹不符",
            _ => "授权：未激活",
        };
    }

    /// <summary>激活窗详情行（LastReason 优先；可用态给操作引导，与既有文案一致）。</summary>
    public static string DetailText()
    {
        string reason = LastReason ?? "";
        if (!string.IsNullOrEmpty(reason)) return reason;
        return CurrentState switch
        {
            LicenseState.Permanent => "本机已永久授权。如需更换授权类型（限期↔永久），"
                + "按当前设备码向厂商签发新激活码，粘贴到下方输入框即可切换（立即生效）。",
            LicenseState.Trial => "试用期内可正常使用。可粘贴新激活码更换授权类型"
                + "（限期↔永久均可，立即生效，无需重启）；到期后需输入新激活码。",
            _ => "请把设备码提供给厂商，收到激活码后粘贴到下方输入框。",
        };
    }
}
