// =========================================================================
// ScannerOptions：扫码枪配置（程序①专用，config.ini [Scanner] 节）
//
// 干什么：把扫码枪的接入方式、串口参数、TCP 网络参数与扫码会话超时集中到
//   一处，供主窗启动装配。两种接入方式（Mode 键，二选一）：
//   ① Tcp（缺省、现场现行）= 以太网 TCP 读码（子模块 ScannerTcpService：
//      上位机作 TCP 客户端连枪侧，按行收条码）；
//   ② Serial = 串口读码（System.IO.Ports，替代接线路径）。
//
// **缺省即现场预置（V0.0.79 建，V0.0.81 收口）**：无 [Scanner] 节的机器（ini 缺键自愈）直接得到
//   现场口径——Mode=Tcp + 枪侧 DTU IP（FieldTcpIp=192.168.1.220）+ 端口 8899
//   + 触发指令留空（=不发）+ **Enabled=true（缺省即连——部署目录重建/新构建
//   覆盖后不再掉回"未启用"，现场不用再手工点一次配置窗保存）。配置窗首次打开回显的就是
//   这套预置值——现场不再手抄 IP（2026-11 现场曾把 DTU 死配置里的旧上位机
//   地址 169.254.254.96 当枪侧 IP 填进来，连接超时的教训→预置值收敛一处）。
//
// 现场 2026-11 起 = TCP 口径（V0.0.78）：SH-5000-GHD 枪本身是串口输出，
//   接一台 **Eport E10 型 DTU**（串口转以太网，Web 配置台 admin/admin，
//   POST /cmd 配置 API）——DTU 侧 socket "netp" 已配成 **TCP-SERVER、
//   本地端口 8899、Rout=uart、maxAccept=3** 并落 NVRAM（改前是 TCP-CLIENT
//   指向已不存在的 169.254.254.96:8888，属遗留死配置，原值备份在探针目录
//   backup_socket_page2.json，如需回滚 DTU 侧改回即可）。枪扫到码 → DTU 把
//   串口字节原样经 TCP 推给所有已连客户端（实测毫秒级、条码文本 + CR 结尾），
//   上位机只管连入收行——**UDP 不再依赖**（DTU 配置里曾有一条 UDP-CLIENT
//   socket（本地口 9645 推往 169.254.204.58:28987），实测可靠性差且该条在
//   DTU 断电重启后不会恢复——未落 NVRAM）。
//   注意：这台枪自动连续读码（扫到即推），**不是基恩士那种"连上后要发 LON
//   触发"的口径**——所以 TCP 模式的触发指令必须留空（子模块 ScanConfig
//   缺省 "LON" 是基恩士口径，接线层必须显式覆盖，见 ScannerBarcodeService）。
//
// 串口方式（Serial，保留作为替代接线路径）定位两种方式（二选一，PortName 优先）：
//   ① PortName 填设备管理器里看到的 COM 号（如 "COM3"）——最直接，换 USB 口要改；
//   ② PortName 留空 + DeviceKeyword 填设备管理器设备名里的关键字（如 "CH340"/
//      "USB-Serial"）→ 服务用 WMI 自动找口，换 USB 口不用改配置。
//   两者都空 = 未配置扫码枪：程序照常运行，只是不读码（日志提示一次）。
//
// 怎么改：新增配置项 = 属性 + Load/Save 两处 + config.sample.ini 同步；
//   ini 键名勿改（现场已按样例部署）。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureLine.Core.Comms;

/// <summary>扫码枪配置（ini [Scanner] 节的内存映像；Serial/Tcp 双模式）。</summary>
public sealed class ScannerOptions
{
    /// <summary>
    /// 是否启用扫码枪。**缺省 true（V0.0.81，出厂缺省=现场口径的收口）**——
    /// 无 [Scanner] 节的机器开箱即按所配方式连接（现场部署目录重建/新构建
    /// 覆盖部署后不再需要手工点一次配置窗保存；这正是 V0.0.79 现场反复踩
    /// "未启用"坑的根因收口）。想不连：配置窗取消【启用】勾选并保存，或
    /// ini 显式写 Enabled=0。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 接入方式（ini 存原文，规范化取 <see cref="NormMode"/>）：
    ///   "Tcp" = 以太网 TCP（上位机作客户端连枪侧 DTU/枪的 TCP 服务口）；
    ///   其它一律按 <see cref="FieldTcpIp"/> 所在的现场口径回 "Tcp"（缺省口径），
    ///   仅显式 "Serial" 走串口方式。TCP/串口两种方式下方参数各自独立生效。
    /// </summary>
    public string Mode { get; set; } = "Tcp";

    /// <summary>
    /// 现场枪侧 DTU 地址真源（TCP-SERVER 8899 所在）：缺省值/config.sample.ini/
    /// 配置窗预置共用此常量，换 DTU/换网段时只改这一处（+现场 ini）。
    /// </summary>
    public const string FieldTcpIp = "192.168.1.220";

    /// <summary>
    /// 【TCP 模式】枪侧 IP（DTU/枪自身的地址）。**缺省 = <see cref="FieldTcpIp"/>
    /// （现场预置）**；ini 空值/缺键在 ReadStr 层自愈回预置（清空不持久），
    /// "未配置 TCP"（HasTcpTarget=false）只在内存态显式置空时出现（测试/防御分支）。
    /// </summary>
    public string TcpIp { get; set; } = FieldTcpIp;

    /// <summary>
    /// 【TCP 模式】枪侧 TCP 服务端口（现场 DTU 已配 TCP-SERVER 8899 并落
    /// NVRAM，缺省 8899 与现场一致；换枪/改 DTU 配置后同步改这里）。
    /// </summary>
    public int TcpPort { get; set; } = 8899;

    /// <summary>
    /// 【TCP 模式】连接成功后的"触发/开始读码"指令（服务自动在指令尾补 CRLF）。
    /// <para>**留空 = 不发（缺省）**——现场 SH-5000-GHD 经 DTU 透传，枪自动连续
    /// 读码，发任何指令都会被当成条码数据写进枪的串口，**绝不能依赖子模块
    /// 缺省 "LON"**；将来接基恩士 SR 这类需要触发指令的 TCP 枪时才填（如 LON）。
    /// </para>
    /// </summary>
    public string TriggerCommand { get; set; } = "";

    // ---------------- 串口方式（Serial）参数 ----------------

    /// <summary>
    /// 【串口模式】固定串口名（如 "COM3"）。填了就固定用该端口；留空则按
    /// <see cref="DeviceKeyword"/> 用 WMI 自动识别（换 USB 口免改配置）。
    /// </summary>
    public string PortName { get; set; } = "";

    /// <summary>
    /// 【串口模式】设备识别关键词（PortName 留空时生效）：设备管理器里设备
    /// 名称包含的关键字（如 USB 转串口芯片 "CH340"/"Prolific"/"USB-Serial"）。
    /// 留空且 PortName 也空 = 未配置扫码枪，不开串口。
    /// </summary>
    public string DeviceKeyword { get; set; } = "";

    /// <summary>【串口模式】波特率（缺省 9600——扫码枪常见出厂值；按扫码枪手册核对）。</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>【串口模式】数据位（缺省 8，扫码枪标准 8N1 口径）。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>【串口模式】停止位字符串（项目约定 "1"/"15"/"2"，缺省 "1"）。</summary>
    public string StopBits { get; set; } = "1";

    /// <summary>【串口模式】校验位（标准枚举名 None/Odd/Even/Mark/Space，缺省 None）。</summary>
    public string Parity { get; set; } = "None";

    /// <summary>
    /// 扫码会话超时秒数（扫了库位码后等产品码的最长间隔，缺省 15）：
    /// 超时自动回到"请扫库位码"，防止界面挂在旧库位误导下一名操作员。
    /// 现场扫码节奏慢可调大，范围 5~600（过小会在慢慢找产品码时被打断）。
    /// 串口/TCP 两种方式共用。
    /// </summary>
    public int SessionTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 串口心跳调试日志开关（排障用，缺省 false）：true 时子模块每个心跳周期
    /// 把 WMI 串口搜索结果打进操作日志，用于排查"插着枪却识别不到"。
    /// TCP 方式不使用（子模块 TCP 服务自带连接失败 30s 节流日志）。
    /// </summary>
    public bool DebugLog { get; set; }

    // ---------------- 规范化（坏值不喂驱动——Link 层唯一出口） ----------------

    /// <summary>
    /// 接入方式规范化：显式 "Serial"（大小写不敏感，去空白）→ "Serial"，
    /// 其余一律回 "Tcp"——缺省口径=现场现行（非法 ini 值落回缺省，与
    /// Theme/PointBitStyle 同款自愈；V0.0.79 起缺省从 Serial 翻为 Tcp）。
    /// </summary>
    public string NormMode =>
        string.Equals(Mode?.Trim(), "Serial", StringComparison.OrdinalIgnoreCase) ? "Serial" : "Tcp";

    /// <summary>
    /// 是否配置了 TCP 目标（TcpIp 非空即算，端口有 <see cref="NormTcpPort"/> 兜底）。
    /// </summary>
    public bool HasTcpTarget => !string.IsNullOrWhiteSpace(TcpIp);

    /// <summary>
    /// 是否配置了可用的接入目标（按规范化后的方式判）：
    ///   Tcp 模式 = TcpIp 非空；Serial 模式 = PortName 或 DeviceKeyword 至少一个非空。
    /// 未配置时主窗显示"扫码未配置"，不启动后台线程。
    /// </summary>
    public bool HasPortTarget =>
        NormMode == "Tcp" ? HasTcpTarget : HasSerialTarget;

    /// <summary>串口定位是否已配置（PortName 或 DeviceKeyword 至少一个非空）。</summary>
    public bool HasSerialTarget =>
        !string.IsNullOrWhiteSpace(PortName) || !string.IsNullOrWhiteSpace(DeviceKeyword);

    // ---------------- "服务未启动"跳过原因（边沿日志唯一文案源） ----------------
    //
    // 为什么收口在本类：启服跳过有两处要写同一行文案——宿主启动装配 InitScanner
    // （Enabled=0 时连 Start 都不调，此前完全静默，现场"胶囊未启用 + 日志无提示"
    // 排障只能开配置文件对；2026-11-07 实测踩坑后补边沿日志）与
    // ScannerBarcodeService.Start（真实调了但被拦）。文案集中一处，改口径不散写。

    /// <summary>未启用跳过日志文案（[Scanner] Enabled=0）。</summary>
    public const string SkipNotEnabledText =
        "扫码枪：未启用（config.ini [Scanner] Enabled=0），跳过启动";

    /// <summary>TCP 方式未配目标跳过日志文案（TcpIp 为空——内存态显式置空才会出现）。</summary>
    public const string SkipNoTargetTcpText =
        "扫码枪：未配置 TCP 目标（[Scanner] TcpIp 为空），跳过启动";

    /// <summary>串口方式未配目标跳过日志文案（PortName 与 DeviceKeyword 均为空）。</summary>
    public const string SkipNoTargetSerialText =
        "扫码枪：未配置串口（[Scanner] PortName 与 DeviceKeyword 均为空），跳过启动";

    /// <summary>
    /// "扫码枪服务不启动"的原因文案（**null = 一切就绪，应启动**）：
    /// 未启用 &gt; 未配目标（按方式给文案）。宿主启动装配与保存热换在"不启动"
    /// 的分支上写这行边沿日志（同源文案，启动日志即可自助定位），启动日志
    /// 再也不会出现"胶囊说未启用、日志里一片安静"的无头案。
    /// </summary>
    public string? SkipReasonText() =>
        !Enabled ? SkipNotEnabledText
        : !HasPortTarget ? (NormMode == "Tcp" ? SkipNoTargetTcpText : SkipNoTargetSerialText)
        : null;

    /// <summary>规范化后的 TCP 端口（1~65535，越界回缺省 8899——现场 DTU 现值）。</summary>
    public int NormTcpPort => TcpPort is >= 1 and <= 65535 ? TcpPort : 8899;

    /// <summary>规范化后的触发指令（去空白；空 = 连上不发任何指令——DTU 透传口径）。</summary>
    public string NormTriggerCommand => TriggerCommand?.Trim() ?? "";

    /// <summary>规范化后的波特率（越界回缺省 9600——坏值不喂给串口驱动）。</summary>
    public int NormBaudRate => BaudRate is >= 2400 and <= 921600 ? BaudRate : 9600;

    /// <summary>规范化后的数据位（5~8，越界回 8）。</summary>
    public int NormDataBits => DataBits is >= 5 and <= 8 ? DataBits : 8;

    /// <summary>规范化后的超时秒数（5~600，越界回 15）。</summary>
    public int NormSessionTimeoutSeconds =>
        SessionTimeoutSeconds is >= 5 and <= 600 ? SessionTimeoutSeconds : 15;

    /// <summary>停止位规范化：只认 "1"/"15"/"2"，其它回 "1"（与子模块约定同口径）。</summary>
    public string NormStopBits => StopBits.Trim() switch
    {
        "15" => "15",
        "2" => "2",
        _ => "1",
    };

    /// <summary>校验位规范化：只认标准枚举名，其它回 None（与子模块约定同口径）。</summary>
    public string NormParity => Parity.Trim().ToLowerInvariant() switch
    {
        "even" => "Even",
        "odd" => "Odd",
        "mark" => "Mark",
        "space" => "Space",
        _ => "None",
    };

    // ---------------- ini 存取（缺键回填缺省，IniFile 自愈） ----------------

    private const string Section = "Scanner";

    /// <summary>从 config.ini 读（缺省值见各属性；非法数值在 Norm* 属性里兜底）。
    /// Enabled 缺省 "1"（V0.0.81 出厂缺省=现场口径：无 [Scanner] 节的机器开箱即连）。</summary>
    public static ScannerOptions Load(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        return new ScannerOptions
        {
            Enabled = IniFile.ReadStr(ini, Section, "Enabled", "1") == "1",
            Mode = IniFile.ReadStr(ini, Section, "Mode", "Tcp"),
            TcpIp = IniFile.ReadStr(ini, Section, "TcpIp", FieldTcpIp).Trim(),
            TcpPort = IniFile.ReadInt(ini, Section, "TcpPort", 8899),
            TriggerCommand = IniFile.ReadStr(ini, Section, "TriggerCommand", ""),
            PortName = IniFile.ReadStr(ini, Section, "PortName", "").Trim(),
            DeviceKeyword = IniFile.ReadStr(ini, Section, "DeviceKeyword", "").Trim(),
            BaudRate = IniFile.ReadInt(ini, Section, "BaudRate", 9600),
            DataBits = IniFile.ReadInt(ini, Section, "DataBits", 8),
            StopBits = IniFile.ReadStr(ini, Section, "StopBits", "1").Trim(),
            Parity = IniFile.ReadStr(ini, Section, "Parity", "None").Trim(),
            SessionTimeoutSeconds = IniFile.ReadInt(ini, Section, "SessionTimeoutSeconds", 15),
            DebugLog = IniFile.ReadStr(ini, Section, "DebugLog", "0") == "1",
        };
    }

    /// <summary>写回 config.ini（扫码枪配置窗保存的唯一落盘口；测试也走这里）。
    /// Mode 落规范化值（"Tcp"/"Serial"），端口落 NormTcpPort——坏值当场治愈。
    /// 注意 TcpIp 落 Trim 值：空值在下次 Load 被自愈回 <see cref="FieldTcpIp"/>
    /// 预置（kernel32 空值=缺键；TcpIp 无"持久未配置"态，想不连 TCP=切
    /// Serial 方式或关 Enabled）。</summary>
    public void Save(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        IniFile.Write(ini, Section, "Enabled", Enabled ? "1" : "0");
        IniFile.Write(ini, Section, "Mode", NormMode);
        IniFile.Write(ini, Section, "TcpIp", TcpIp.Trim());
        IniFile.Write(ini, Section, "TcpPort", NormTcpPort.ToString());
        IniFile.Write(ini, Section, "TriggerCommand", NormTriggerCommand);
        IniFile.Write(ini, Section, "PortName", PortName.Trim());
        IniFile.Write(ini, Section, "DeviceKeyword", DeviceKeyword.Trim());
        IniFile.Write(ini, Section, "BaudRate", NormBaudRate.ToString());
        IniFile.Write(ini, Section, "DataBits", NormDataBits.ToString());
        IniFile.Write(ini, Section, "StopBits", NormStopBits);
        IniFile.Write(ini, Section, "Parity", NormParity);
        IniFile.Write(ini, Section, "SessionTimeoutSeconds", NormSessionTimeoutSeconds.ToString());
        IniFile.Write(ini, Section, "DebugLog", DebugLog ? "1" : "0");
    }

    // ---------------- 缺键自愈落盘（V0.0.81） ----------------
    //
    // 为什么：出厂缺省=现场口径（Tcp/预置 IP/8899/启用）最初只活在内存缺省里，
    //   config.ini 里一行都没有——现场拿文件核对/备份看到的是"程序没配过扫码枪"
    //   （2026-11-07 实录：部署目录重建 → ini 没了 → 配置窗 UI 上勾着启用（预填）
    //   实际没启动，用户点一次保存才回正）；此函数把"缺省=字面落盘"，文件所见
    //   即程序所得，部署目录重建也不再依赖手工操作。

    /// <summary>
    /// [Scanner] 节缺键自愈落盘：把 config.ini 里"根本没写"的键按出厂缺省值
    /// （现场口径）补写进去。**幂等**——已存在的键一律不碰（显式 Enabled=0、
    /// 用户改过的 TcpIp、手工留空的串口键都原样保留，kernel32 层"空字符串值"
    /// 与"缺键"无法区分、一视同仁当缺键回填，与 Load 的"无持久未配置态"拍板
    /// 同构）；宿主启动装配（InitScanner）调用一次。不伦配置窗是否打开过，
    /// 现场打开 config.ini 直接看到完整扫码枪段。
    /// </summary>
    public static void EnsureDefaultsPersisted(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        var defaults = new ScannerOptions();   // 当前缺省=现场口径（Enabled 含 1）
        // 哨兵探测缺键：IniFile.ReadStr 缺键返回缺省值——用不可能被现场使用的
        // 哨兵当"缺省"即可区分"键在"（返回实值≠哨兵）与"键缺"（返回哨兵）。
        const string unset = "&missing-key&";
        void Heal(string key, string value)
        {
            if (IniFile.ReadStr(ini, Section, key, unset) == unset)
                IniFile.Write(ini, Section, key, value);
        }
        Heal("Enabled", defaults.Enabled ? "1" : "0");
        Heal("Mode", defaults.NormMode);
        Heal("TcpIp", defaults.TcpIp);
        Heal("TcpPort", defaults.NormTcpPort.ToString());
        Heal("TriggerCommand", defaults.NormTriggerCommand);
        Heal("PortName", defaults.PortName);
        Heal("DeviceKeyword", defaults.DeviceKeyword);
        Heal("BaudRate", defaults.NormBaudRate.ToString());
        Heal("DataBits", defaults.NormDataBits.ToString());
        Heal("StopBits", defaults.NormStopBits);
        Heal("Parity", defaults.NormParity);
        Heal("SessionTimeoutSeconds", defaults.NormSessionTimeoutSeconds.ToString());
        Heal("DebugLog", "0");
    }
}
