// =========================================================================
// CuringMesOptions：单线过账配置（程序①专用，config.ini [CuringMes] 节）
//
// 干什么：固化线单线过账的全部可调参数集中一处——总开关、工位选择、
//   固化时间单位、进站时机、中途取件口径、静态参数（MAC_NO 等）。
// 为什么独立成类不塞 AppConfig：这是程序①的业务配置，主程序（整线网关）
//   用不到；且过账配置窗保存后要"重建服务即生效"（实例化传递，不依赖
//   静态刷新），与 AppConfig 的启动一次性装配口径不同。
//
// 【W68 终版未定 → 全部可配置】现场联调时点点鼠标改配置即可，代码零改动：
//   ① URL：沿用 [MES] Url_API0027/0030/0032（与主程序同键，跨程序一致）；
//   ② 固化时间单位：老项目传秒（GH 计时器），W68 终版若要分钟改 TimeUnit；
//   ③ 进站时机：老项目"装载"是 PLC 命令字，新项目映射到托盘到位——若现场
//     口径是"开计时才算进站"，PostInAt 改 Start（界面上拉一下即可）；
//   ④ 中途取件（计时作废=强制出料语义）：老项目 Case3 照常上传，若现场
//     不要，PostAbortOut 关掉；
//   ⑤ 静态参数（MAC_NO/MAC_LOC_NO/PLANT/PRA_CODE/CHECK_RESULT）：本节配置
//     优先，留空回退 config\mes_params.json 记忆值（主程序调试窗可维护）。
//
// 怎么改：新增配置项 = 属性 + Load/Save 两处 + 过账配置窗加控件 + 校验器；
//   ini 键名勿改（现场已按样例部署）。
// =========================================================================

using PlcMesBridge.Core.Infrastructure;

namespace PlcMesBridge.Core.Curing;

/// <summary>固化时间上传单位（API0027 PRA_Value）。Seconds=老项目口径（默认）。</summary>
public enum CuringTimeUnit
{
    Seconds,
    Minutes,
}

/// <summary>进站（API0030 ACTION=I）上传时机。Arrive=托盘到位（默认）；Start=开计时。</summary>
public enum PostInAt
{
    Arrive,
    Start,
}

/// <summary>单线过账配置（ini [CuringMes] 节的内存映像，过账配置窗读写）。</summary>
public sealed class CuringMesOptions
{
    /// <summary>过账总开关。false=只程控不过账（缺省——现场配好 MES 再开）。</summary>
    public bool Enabled { get; set; }

    /// <summary>自动过账取工单哪个工位："R" / "L"（其它值按 R 兜底）。</summary>
    public string Side { get; set; } = "R";

    /// <summary>固化时间上传单位（API0027 PRA_Value）：秒（老项目口径）/分钟。</summary>
    public CuringTimeUnit TimeUnit { get; set; } = CuringTimeUnit.Seconds;

    /// <summary>进站上传时机：托盘到位 / 开计时（门控放行后）。</summary>
    public PostInAt InAt { get; set; } = PostInAt.Arrive;

    /// <summary>
    /// 计时中取件（计时作废=强制出料语义）是否照常传出站链。
    /// true=老项目 Case3 口径（默认）；false=中途取件只记日志不上报。
    /// </summary>
    public bool PostAbortOut { get; set; } = true;

    // ---- 静态参数（配置优先，空=回退 MesParamStore 记忆值） ----

    public string MacNo { get; set; } = "";
    public string MacLocNo { get; set; } = "";
    public string Plant { get; set; } = "";
    public string PraCode { get; set; } = "";
    public string CheckResult { get; set; } = "";

    /// <summary>工位规范化：非 L 一律按 R（安全侧——错传 R 工位工单好过空值）。</summary>
    public static string NormSide(string? side) =>
        string.Equals(side?.Trim(), "L", StringComparison.OrdinalIgnoreCase) ? "L" : "R";

    // ---------------- ini 存取（缺键回填缺省，IniFile 自愈） ----------------

    private const string Section = "CuringMes";

    /// <summary>从 config.ini 读（缺省值见各属性；非法枚举值回缺省不抛）。</summary>
    public static CuringMesOptions Load(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        return new CuringMesOptions
        {
            Enabled = IniFile.ReadStr(ini, Section, "Enabled", "0") == "1",
            Side = NormSide(IniFile.ReadStr(ini, Section, "Side", "R")),
            TimeUnit = IniFile.ReadStr(ini, Section, "TimeUnit", "Seconds")
                .Equals("Minutes", StringComparison.OrdinalIgnoreCase)
                ? CuringTimeUnit.Minutes : CuringTimeUnit.Seconds,
            InAt = IniFile.ReadStr(ini, Section, "PostInAt", "Arrive")
                .Equals("Start", StringComparison.OrdinalIgnoreCase)
                ? PostInAt.Start : PostInAt.Arrive,
            PostAbortOut = IniFile.ReadStr(ini, Section, "PostAbortOut", "1") == "1",
            MacNo = IniFile.ReadStr(ini, Section, "MacNo", "").Trim(),
            MacLocNo = IniFile.ReadStr(ini, Section, "MacLocNo", "").Trim(),
            Plant = IniFile.ReadStr(ini, Section, "Plant", "").Trim(),
            PraCode = IniFile.ReadStr(ini, Section, "PraCode", "").Trim(),
            CheckResult = IniFile.ReadStr(ini, Section, "CheckResult", "").Trim(),
        };
    }

    /// <summary>写回 config.ini（过账配置窗保存时调）。目录不存在由 IniFile 兜底。</summary>
    public void Save(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        IniFile.Write(ini, Section, "Enabled", Enabled ? "1" : "0");
        IniFile.Write(ini, Section, "Side", NormSide(Side));
        IniFile.Write(ini, Section, "TimeUnit", TimeUnit == CuringTimeUnit.Minutes ? "Minutes" : "Seconds");
        IniFile.Write(ini, Section, "PostInAt", InAt == PostInAt.Start ? "Start" : "Arrive");
        IniFile.Write(ini, Section, "PostAbortOut", PostAbortOut ? "1" : "0");
        IniFile.Write(ini, Section, "MacNo", MacNo.Trim());
        IniFile.Write(ini, Section, "MacLocNo", MacLocNo.Trim());
        IniFile.Write(ini, Section, "Plant", Plant.Trim());
        IniFile.Write(ini, Section, "PraCode", PraCode.Trim());
        IniFile.Write(ini, Section, "CheckResult", CheckResult.Trim());
    }
}
