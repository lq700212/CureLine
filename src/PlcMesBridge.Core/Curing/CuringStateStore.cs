// =========================================================================
// CuringStateStore：仓位计时状态持久化（断电续算，PersistMinutes 开关的落盘件）
//
// 干什么：把状态机里"进行中"的仓位（计时中/已放行）写成 config/curingstate.json，
//   重启对齐时读回续算。什么时候写：状态机每次状态变化（开计时/放行/取件清空）
//   全量重写——文件就几十行，边沿级频率，比定时落盘简单且不丢边沿。
// 为什么独立一个 Store：与策略（curingpolicy.json，配置）不同，这是**运行时
//   数据**——现场自动生成、不入库、删了也无妨（无非回退"重计"口径）。
// 怎么改：新增持久化字段 = 记录类加属性 + 状态机写点补赋值，两处一致；
//   缺/坏文件回 null（带原因列表），调用方兜底重计——与点表/策略 Store 同款自愈语义。
// =========================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMesBridge.Core.Infrastructure;

namespace PlcMesBridge.Core.Curing;

/// <summary>单仓位持久化记录（只存恢复所需最小集：相位/起点/锁定时长）。</summary>
public sealed class CuringSlotRecord
{
    public int Rack { get; set; }
    public int Level { get; set; }

    /// <summary>"Curing"=计时中（按 StartedAt+Minutes 续算）；"Done"=已放行（恢复绿灯）。</summary>
    public string Phase { get; set; } = "";

    /// <summary>开计时那一刻的绝对时间（墙钟——断电期间流逝自然计入固化时长）。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>开计时时锁定的时长（分钟）——恢复用记录值，不用当前策略（热切不干扰在制品）。</summary>
    public int Minutes { get; set; }
}

/// <summary>持久化信封：全部进行中仓位的快照。</summary>
public sealed class CuringStateBook
{
    public List<CuringSlotRecord> Slots { get; set; } = new();
}

public static class CuringStateStore
{
    /// <summary>落盘路径（config/curingstate.json；AppPaths.ConfigDir 由启动时确保存在）。</summary>
    public static string DefaultPath => Path.Combine(AppPaths.ConfigDir, "curingstate.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义，现场可肉眼检查
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 读持久化状态。缺文件/坏 JSON 回 null（reasons 带原因，调用方记日志）——
    /// 调用方兜底走"重新计时"口径，永不因持久化文件问题卡流程。
    /// </summary>
    public static CuringStateBook? Load(List<string>? reasons = null)
    {
        try
        {
            if (!File.Exists(DefaultPath))
            {
                reasons?.Add("计时状态文件不存在（首次启动或已清空），按重新计时口径。");
                return null;
            }
            var json = File.ReadAllText(DefaultPath);
            var book = JsonSerializer.Deserialize<CuringStateBook>(json, Options);
            if (book == null)
            {
                reasons?.Add("计时状态文件内容为空，按重新计时口径。");
                return null;
            }
            return book;
        }
        catch (Exception ex)
        {
            reasons?.Add($"计时状态文件损坏（{ex.Message}），按重新计时口径。");
            return null;
        }
    }

    /// <summary>全量落盘（状态机状态变化时调）。失败抛给调用方记日志——落盘失败不影响流程。</summary>
    public static void Save(CuringStateBook book)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(book, Options));
    }
}
