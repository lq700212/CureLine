// =========================================================================
// WorkOrderStore：固化程控工单信息（config/workorder.json 存取 + 自愈）
//
// 干什么：工单页（对照 docs/参考界面 工单信息 HMI）的数据模型与持久化——
//   R/L 双工位各 { 途程单号 RC_NO / 预设数量 / 已生产数量 } + 基础信息
//   员工工号 EMP_NO + 批次物料条码 BC_NO + 制程代码 PT_NO×2 + 物料上传使能。
// 为什么独立 JSON：工单是"现场作业状态"不是配置——操作员随时改，程序重启
//   要还在（当前工单接着干）；与点表/策略同款 JSON 自愈语义（缺/坏回 null，
//   调用方兜缺省并记日志）。将来单线过账（P3.2 后半）从本模型映射 MES 16
//   字段（RC_NO/PT_NO/EMP_NO/BC_NO 与 MesParamStore 字段名刻意对齐）。
// 怎么改：新增工单字段 = 模型加属性 + 工单窗加控件 + VM Sync/Flush 两处，
//   过账映射处同步；字段名与 MesParamStore 对齐的（RC_NO 等）勿改名。
// =========================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMesBridge.Core.Infrastructure;

namespace PlcMesBridge.Core.Curing;

/// <summary>单工位工单（R/L 各一份）：途程单号 + 预设/已生产数量。</summary>
public sealed class WorkOrderSlot
{
    /// <summary>途程单号（RC_NO；与 MES 参数记忆字段名对齐）。</summary>
    public string RcNo { get; set; } = "";

    /// <summary>预设数量（本工单计划量）。</summary>
    public int PresetQty { get; set; }

    /// <summary>已生产数量（过账成功累计；计数清零按钮归零）。</summary>
    public int ProducedQty { get; set; }
}

/// <summary>工单信息整本（程序①当前作业状态）。</summary>
public sealed class WorkOrderBook
{
    public WorkOrderSlot Right { get; set; } = new();
    public WorkOrderSlot Left { get; set; } = new();

    /// <summary>员工工号（EMP_NO；操作员登录后可带出，也可手改）。</summary>
    public string EmpNo { get; set; } = "";

    /// <summary>物料条码（BC_NO；批次物料上报用）。</summary>
    public string BcNo { get; set; } = "";

    /// <summary>制程代码-1（PT_NO）。</summary>
    public string PtNo1 { get; set; } = "";

    /// <summary>制程代码-2（PT_NO）。</summary>
    public string PtNo2 { get; set; } = "";

    /// <summary>物料上传使能（参考 HMI"物料上传使能"开关；动作接线在过账轮）。</summary>
    public bool UploadEnabled { get; set; }
}

public static class WorkOrderStore
{
    /// <summary>落盘路径（config/workorder.json）。</summary>
    public static string DefaultPath => Path.Combine(AppPaths.ConfigDir, "workorder.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 读工单。缺文件/坏 JSON 回 null（reasons 带原因，调用方兜缺省并记日志）——
    /// 与点表/策略 Store 同款自愈语义，永不因文件问题卡流程。
    /// </summary>
    public static WorkOrderBook? Load(List<string>? reasons = null)
    {
        try
        {
            if (!File.Exists(DefaultPath))
            {
                reasons?.Add("工单文件不存在（首次启动），按空工单处理。");
                return null;
            }
            var json = File.ReadAllText(DefaultPath);
            var book = JsonSerializer.Deserialize<WorkOrderBook>(json, Options);
            if (book == null)
            {
                reasons?.Add("工单文件内容为空，按空工单处理。");
                return null;
            }
            return book;
        }
        catch (Exception ex)
        {
            reasons?.Add($"工单文件损坏（{ex.Message}），按空工单处理。");
            return null;
        }
    }

    /// <summary>全量落盘（工单窗保存时调）。失败抛给调用方记日志。</summary>
    public static void Save(WorkOrderBook book)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(book, Options));
    }

    /// <summary>空工单（自愈兜底；数量归零、文本全空）。</summary>
    public static WorkOrderBook BuildDefault() => new();
}
