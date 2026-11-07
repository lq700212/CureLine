// =========================================================================
// SlotBinding：库位 ↔ 产品条码绑定（扫码工序的落盘件）
//
// 干什么：记录"哪个库位（架×层）当前放的是哪个产品"——操作员先扫库位码
//   （A01~J12），再扫产品二维码，绑定关系写进 config/slotbindings.json；
//   托盘离开（取件）时由会话Remove清掉，下次入库必须重新扫码。
// 为什么独立一个 Store：与计时状态（curingstate.json，运行时数据）不同，
//   这是**生产追溯数据**——现场要能看到"这个库位放的是哪个产品"，
//   程序重启后绑定不能丢（操作员接着扫码继续生产，不用重扫在架托盘）。
//   但也不是配置：删掉文件只丢绑定记录，不影响程控流程（状态机零依赖）。
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

/// <summary>单条库位绑定记录（架×层 → 产品条码）。</summary>
public sealed class SlotBindingRecord
{
    /// <summary>全局架号（点表口径 1~N，双架时第二台从 11 起）。</summary>
    public int Rack { get; set; }

    /// <summary>层号（1~3，上中下）。</summary>
    public int Level { get; set; }

    /// <summary>产品条码原文（扫码枪传来什么就存什么，去首尾空白后保存）。</summary>
    public string ProductCode { get; set; } = "";

    /// <summary>绑定时刻（墙钟，现场对账用）。</summary>
    public DateTimeOffset BoundAt { get; set; }

    /// <summary>库位显示码（A01~J12）——计算属性不落盘（JsonIgnore）。</summary>
    [JsonIgnore]
    public string LocationCode => SlotLocationCode.EncodeInRack(Rack, Level);
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
            // 脏数据防御：空条码/非法架层的记录直接剔除（手改坏了不至于显示幽灵绑定）。
            book.Bindings.RemoveAll(b =>
                string.IsNullOrWhiteSpace(b.ProductCode) || b.Rack < 1 || b.Level < 1);
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
