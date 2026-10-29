// =========================================================================
// CuringPolicy：固化工艺策略（仓位状态机的"参数表"）
//
// 干什么：定义"托盘到位后计多久、光栅要不要参与判定"的工艺参数。
//   时长支持三级覆盖粒度（三种情况都能配、可混用）：
//     全局 GlobalMinutes 打底 → 每层 LevelMinutes 覆盖 → 每仓位 SlotMinutes 覆盖，
//     解析优先级 仓位 > 层 > 全局（ResolveMinutes 一处收敛，禁止各写一份）。
// 为什么不用"三选一模式"枚举：三级覆盖天然兼容三种粒度——只配全局=
//   全架一个时长，精细到单仓位也行，现场按工艺自由度自选；还避免
//   "选了每仓位模式但只配了 3 个仓位、其余仓位无值可用"的坑。
// 怎么改：新增工艺参数在这里加字段 + Validator 加规则 + 状态机取值处
//   同步；策略库持久化见 CuringPolicyStore，消费方见 SlotCuringMachine。
// =========================================================================

namespace CureLine.Core.Curing;

/// <summary>
/// 固化工艺策略（可多策略存库，激活一个生效，见 CuringPolicyBook）。
/// </summary>
public sealed class CuringPolicy
{
    /// <summary>策略名（库内唯一，激活与界面选择的键）。</summary>
    public string Name { get; set; } = "";

    /// <summary>全局固化时长（分钟）。三级覆盖的打底值。</summary>
    public int GlobalMinutes { get; set; } = 120;

    /// <summary>每层覆盖时长（key=层号 1~4）。没配的层回退全局。</summary>
    public Dictionary<int, int> LevelMinutes { get; set; } = new();

    /// <summary>每仓位覆盖时长（key="架-层"，见 SlotKey）。没配的仓位回退层→全局。</summary>
    public Dictionary<string, int> SlotMinutes { get; set; } = new();

    /// <summary>
    /// 光栅门控开关。false=仅监控显示（托盘到位即开计时，极简口径）；
    /// true=左右光栅都灭且托盘在架才开计时（防人手还在架内就计时）。
    /// 没绑光栅的仓位门控自动放行（快照取不到=视为让空，不卡流程）。
    /// </summary>
    public bool GrillGate { get; set; }

    /// <summary>
    /// 计时状态持久化开关（断电续算）。false=缺省，断电/重启后托盘在架的仓位
    /// 重新计时（宁可重计也不放行没固化够的货）；true=进行中仓位（计时中/已放行）
    /// 随状态变化落盘 config/curingstate.json，重启对齐时按记录续算——断电期间的
    /// 时间也计入固化时长（适合"炉内保温、断电不停固"的工艺口径）。
    /// 开关跟激活策略走：热切换策略即切换持久化行为，无需重启。
    /// </summary>
    public bool PersistMinutes { get; set; }

    /// <summary>
    /// 是否预置策略（出厂模板）。true=预置：策略窗可查看/编辑参数、可"套用为
    /// 自定义"，但不能激活/不能删除/不能直接保存（激活只认自定义策略——预置
    /// 是起点模板，保证出厂推荐永远能回溯）；false=自定义：全套操作可用。
    /// 老版本 JSON 无此字段，反序列化缺省 false=全按自定义（兼容零迁移）。
    /// </summary>
    public bool IsPreset { get; set; }

    /// <summary>解析某仓位的有效时长：仓位 > 层 > 全局（值 ≤0 视为没配）。</summary>
    public int ResolveMinutes(int rack, int level)
    {
        if (SlotMinutes.TryGetValue(SlotKey(rack, level), out var m) && m > 0)
            return m;
        if (LevelMinutes.TryGetValue(level, out m) && m > 0)
            return m;
        return GlobalMinutes;
    }

    /// <summary>仓位键文本（"架-层"），SlotMinutes 字典与界面共用一处拼装。</summary>
    public static string SlotKey(int rack, int level) => $"{rack}-{level}";
}

/// <summary>策略库（持久化信封）：多策略 + 激活名。策略窗保存/激活整体落盘。</summary>
public sealed class CuringPolicyBook
{
    public List<CuringPolicy> Policies { get; set; } = new();

    /// <summary>激活策略名（空=未激活，ResolveActive 回退第一个）。</summary>
    public string ActiveName { get; set; } = "";

    /// <summary>
    /// 取当前生效策略：按激活名匹配（Trim + 忽略大小写）→ 库空回 null。
    /// 未激活或激活名失效时回第一个（现场删改策略名不至于让程控停摆）。
    /// </summary>
    public CuringPolicy? ResolveActive()
    {
        if (Policies.Count == 0)
            return null;
        var active = Policies.FirstOrDefault(p => string.Equals(
            (p.Name ?? "").Trim(), (ActiveName ?? "").Trim(),
            StringComparison.OrdinalIgnoreCase));
        return active ?? Policies[0];
    }
}

/// <summary>校验结果（Ok + 全部错误行），ToString 直接可弹框。</summary>
public sealed record CuringValidation(bool Ok, IReadOnlyList<string> Errors)
{
    public override string ToString() => Ok ? "校验通过" : string.Join("\r\n", Errors);
}

/// <summary>
/// 工艺策略校验器（保存前必跑，错不落盘）。规则：
///   策略名非空；时长 ≥1 分钟；层号 1~4（每块 16 位 ÷ 每层 4 位，与 IO 表对齐）；
///   仓位键"架-层"格式合法（架 1~99、层 1~4）；库内策略名唯一（重名会让激活二义）。
/// </summary>
public static class CuringPolicyValidator
{
    public const int MaxLevel = 4;
    public const int MaxRack = 99;

    /// <summary>单策略校验。</summary>
    public static CuringValidation Validate(CuringPolicy? policy)
    {
        var errors = new List<string>();
        if (policy == null)
        {
            errors.Add("策略为空。");
            return new CuringValidation(false, errors);
        }
        if (string.IsNullOrWhiteSpace(policy.Name))
            errors.Add("策略名不能为空。");
        if (policy.GlobalMinutes < 1)
            errors.Add($"策略[{policy.Name}] 全局时长需 ≥1 分钟（当前 {policy.GlobalMinutes}）。");
        foreach (var (level, minutes) in policy.LevelMinutes)
        {
            if (level is < 1 or > MaxLevel)
                errors.Add($"策略[{policy.Name}] 每层覆盖的层号 {level} 越界（1~{MaxLevel}）。");
            if (minutes < 1)
                errors.Add($"策略[{policy.Name}] 层{level} 的覆盖时长需 ≥1 分钟（当前 {minutes}）。");
        }
        foreach (var (key, minutes) in policy.SlotMinutes)
        {
            var (rack, level, ok) = ParseSlotKey(key);
            if (!ok)
                errors.Add($"策略[{policy.Name}] 仓位键 \"{key}\" 格式不对（应为 架-层，如 3-2）。");
            else
            {
                if (rack is < 1 or > MaxRack)
                    errors.Add($"策略[{policy.Name}] 仓位 \"{key}\" 架号越界（1~{MaxRack}）。");
                if (level is < 1 or > MaxLevel)
                    errors.Add($"策略[{policy.Name}] 仓位 \"{key}\" 层号越界（1~{MaxLevel}）。");
            }
            if (minutes < 1)
                errors.Add($"策略[{policy.Name}] 仓位 \"{key}\" 的覆盖时长需 ≥1 分钟（当前 {minutes}）。");
        }
        return new CuringValidation(errors.Count == 0, errors);
    }

    /// <summary>整库校验：逐策略 + 策略名唯一。</summary>
    public static CuringValidation ValidateBook(CuringPolicyBook? book)
    {
        var errors = new List<string>();
        if (book == null)
        {
            errors.Add("策略库为空。");
            return new CuringValidation(false, errors);
        }
        if (book.Policies.Count == 0)
            errors.Add("策略库至少要有一个策略。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in book.Policies)
        {
            var one = Validate(p);
            errors.AddRange(one.Errors);
            if (!string.IsNullOrWhiteSpace(p.Name) && !seen.Add(p.Name.Trim()))
                errors.Add($"策略名 \"{p.Name}\" 重复（重名会让激活二义），请改名。");
        }
        return new CuringValidation(errors.Count == 0, errors);
    }

    /// <summary>解析仓位键"架-层"（架 1~99、层 1~4）。格式不对 ok=false。</summary>
    private static (int Rack, int Level, bool Ok) ParseSlotKey(string key)
    {
        var parts = (key ?? "").Split('-');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var rack) ||
            !int.TryParse(parts[1], out var level))
            return (0, 0, false);
        return (rack, level, true);
    }
}
