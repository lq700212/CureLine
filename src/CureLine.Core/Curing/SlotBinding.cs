// =========================================================================
// SlotBinding：库位 ↔ 产品条码绑定（扫码工序的落盘件）
//
// 干什么：记录"哪个库位（架×层）当前放了哪些产品"——操作员先扫库位码
//   （A01~J12），再逐个扫产品二维码（**一个库位可以放多个产品**），
//   全部扫完后**再扫一次同一个库位码**=上料结束；绑定关系写进
//   config/slotbindings.json。托盘离开（取件）时由会话 Remove 清掉，
//   下次入库必须重新扫码。
// 上料确认（LoadConfirmed）：第一次扫库位码=上料开始，第二次扫同一个
//   库位码=上料结束。库位在"上料未确认"期间**不报光栅报警**——上料时
//   操作员的手和产品会挡住光栅，报警是误报（客户口径，见 SlotCuringMachine
//   的 loadPendingProvider 闸）。确认之后光栅被挡才是真报警。
// 为什么独立一个 Store：与计时状态（curingstate.json，运行时数据）不同，
//   这是**生产追溯数据**——现场要能看到"这个库位放了哪几个产品"，
//   程序重启后绑定不能丢（操作员接着扫码继续生产，不用重扫在架托盘）。
//   但也不是配置：删掉文件只丢绑定记录，不影响程控流程（状态机零依赖）。
//   历史流水（谁固化了多久）另有 SQLite 追溯库（SlotProductTraceStore），
//   本文件只管"当前在架"这一层，两者互补不替代。
// 怎么改：新增绑定字段（如批次号/工单号）= 记录类加属性 + 会话绑定处赋值，
//   两处一致；缺/坏文件回 null 带原因（调用方兜底空簿），与点表/策略/
//   计时状态三件 Store 同款自愈语义。
//
// 编码口径：库位显示一律走 SlotLocationCode.EncodeInRack（按架内站号换算，
//   双架时每台架各自一套 A01~J12），与主窗卡片标题同源，禁止另拼字符串。
// =========================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Curing;

/// <summary>单条库位绑定记录（架×层 → 产品条码清单 + 上料确认状态）。</summary>
public sealed class SlotBindingRecord
{
    /// <summary>全局架号（点表口径 1~N，双架时第二台从 11 起）。</summary>
    public int Rack { get; set; }

    /// <summary>层号（1~3，上中下）。</summary>
    public int Level { get; set; }

    /// <summary>
    /// 该库位在架产品的 SN 清单（一个库位可放多个产品，逐个扫码累加）。
    /// 顺序=扫码顺序（现场对账时"第一个放的是哪个"有用）。
    /// </summary>
    public List<string> ProductCodes { get; set; } = new();

    /// <summary>首次绑定时刻（墙钟，现场对账用）。</summary>
    public DateTimeOffset BoundAt { get; set; }

    /// <summary>最近一次变更时刻（追加产品/确认上料都刷新）。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 上料是否已确认（第二次扫同一个库位码=上料结束=true）。
    /// false=上料未确认（正在装产品）——此期间该库位不报光栅报警。
    /// </summary>
    public bool LoadConfirmed { get; set; }

    /// <summary>上料确认时刻（null=未确认）。</summary>
    public DateTimeOffset? LoadConfirmedAt { get; set; }

    /// <summary>
    /// 旧版单值产品码（V0.0.97 之前的落盘字段：一条记录只存一个产品码）。
    /// **只作一次性数据迁移用**：读取时若 ProductCodes 为空而本字段有值，
    /// 迁进 ProductCodes 后清空本字段；新写入一律不填（落盘时不写 null 字段）。
    /// 迁移完盘上即新格式，本字段此后恒为空——不是并存的第二套口径。
    /// </summary>
    [JsonPropertyName("productCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyProductCode { get; set; }

    /// <summary>库位显示码（A01~J12）——计算属性不落盘（JsonIgnore）。</summary>
    [JsonIgnore]
    public string LocationCode => SlotLocationCode.EncodeInRack(Rack, Level);

    /// <summary>该库位已绑产品件数（0=未绑定）。</summary>
    [JsonIgnore]
    public int ProductCount => ProductCodes.Count;
}

/// <summary>绑定簿信封：全部库位绑定的快照（JSON 根对象）。</summary>
public sealed class SlotBindingBook
{
    public List<SlotBindingRecord> Bindings { get; set; } = new();
}

/// <summary>
/// 绑定簿存取（config/slotbindings.json）。
/// 为什么全量重写：绑定是低频边沿（每次扫码/取件一条），文件几十行，
/// 全量写简单且不会出现"跟新一半断电"的半更新（与 CuringStateStore 同款口径）。
/// </summary>
public static class SlotBindingStore
{
    /// <summary>落盘路径（config/slotbindings.json；config 目录由启动时确保存在）。</summary>
    public static string DefaultPath => Path.Combine(AppPaths.ConfigDir, "slotbindings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 中文不转义：现场可以直接用记事本打开核对绑定内容。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 读绑定簿。缺文件/坏 JSON 回 null（reasons 带原因，调用方记日志后按空簿起）——
    /// 绑定丢失只影响追溯显示，程控与过账零依赖，绝不因文件问题卡启动。
    /// </summary>
    public static SlotBindingBook? Load(List<string>? reasons = null)
    {
        try
        {
            if (!File.Exists(DefaultPath))
            {
                reasons?.Add("库位绑定文件不存在（首次启动或已清空），按空绑定簿启动。");
                return null;
            }
            var json = File.ReadAllText(DefaultPath);
            var book = JsonSerializer.Deserialize<SlotBindingBook>(json, Options);
            if (book == null)
            {
                reasons?.Add("库位绑定文件内容为空，按空绑定簿启动。");
                return null;
            }
            // 旧格式一次性迁移：老盘上一条记录只存一个 productCode，迁进
            // ProductCodes 清单后清空旧字段——盘上即变新格式，此后不再读旧字段
            // （不是两套并存口径，是一次性搬家）。
            foreach (var b in book.Bindings)
            {
                if (b.ProductCodes.Count == 0 && !string.IsNullOrWhiteSpace(b.LegacyProductCode))
                    b.ProductCodes.Add(b.LegacyProductCode.Trim());
                b.LegacyProductCode = null;
                b.ProductCodes.RemoveAll(string.IsNullOrWhiteSpace);
            }
            // 脏数据防御：非法架层的记录直接剔除（手改坏了不至于显示幽灵绑定）。
            book.Bindings.RemoveAll(b => b.Rack < 1 || b.Level < 1);
            return book;
        }
        catch (Exception ex)
        {
            reasons?.Add($"库位绑定文件损坏（{ex.Message}），按空绑定簿启动。");
            return null;
        }
    }

    /// <summary>
    /// 全量落盘。失败向上抛给调用方记日志——调用方（会话）捕获后只记一行，
    /// 内存绑定照常生效（落盘失败不影响本次生产，重启重扫即可）。
    /// </summary>
    public static void Save(SlotBindingBook book)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(book, Options));
    }

    /// <summary>清空绑定文件（会话测试/现场重置用；删文件即回到"全部未绑定"）。</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(DefaultPath))
                File.Delete(DefaultPath);
        }
        catch
        {
            // 删不掉不阻塞（下次 Save 会整文件覆盖）。
        }
    }
}
