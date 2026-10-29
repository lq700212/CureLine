// =========================================================================
// CureRecordStore：固化库位记录（复刻自老项目 Module1.IDRecord + Form1.UpdateStats）
//
// 表 PLCtable(Time, 库位ID, 产品ID, Mark)：
// ① 装载时 delete+insert（同库位覆盖，老项目 IDRecord 语义）；
// ② 卸载/强制出料时 delete；③ 资料获取时 select 回写 PLC。
// 统计 UpdateStats 拆成两半：LoadStatsRows 搬数据（DB相关），
// ComputeStats 纯算（可单元测试，见 CureStatsTests）。
//
// 统计口径（老项目原样，勿"优化"）：
// - 预计完成 = 进入时间 + 当前固化秒（D30005 实时值，不是入库时快照）；
// - 完成/静置按产品ID第4字符 L/R 分左右（Substring(3,1)，短于4字符会抛，
//   老项目无保护；新项目加长度保护，不合规 ID 计 R——行为差异已在测试锁定）；
// - 明日可完成：finishTime.Day == 明天.Day 且 Hour <= 8（跨月只比 Day 数，
//   老项目原样，1号/月末边界有误差， Heritage 语义不改，注释留档）。
// =========================================================================

using System.Data;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Data;

/// <summary>统计结果（缓存供中英切换重绘，老项目 currentXXX 变量对应）。</summary>
public class CureStats
{
    public int Total;
    public int DoneL, DoneR;
    public int RestingL, RestingR;
    public int TomorrowL, TomorrowR;
}

public class CureRecordStore
{
    private readonly SqliteHelper _db;

    public CureRecordStore(SqliteHelper db)
    {
        _db = db;
    }

    /// <summary>
    /// 入库（老项目 IDRecord）：同库位先删后插。参数化，防注入。
    /// Time 取当前时间（老项目 Now.ToString("yyyy-MM-dd HH:mm:ss")）。
    /// </summary>
    public void SaveRecord(string binId, string productIds, string mark)
    {
        _db.ExecuteNonQuery("delete from PLCtable where 库位ID=@id",
            new Dictionary<string, object?> { ["id"] = binId });
        _db.ExecuteNonQuery(
            "insert into PLCtable (Time,库位ID,产品ID,Mark) values (@Time,@库位ID,@产品ID,@Mark)",
            new Dictionary<string, object?>
            {
                ["Time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["库位ID"] = binId,
                ["产品ID"] = productIds,
                ["Mark"] = mark,
            });
    }

    /// <summary>出库删记录（卸载/强制出料）。返回受影响行数。</summary>
    public int DeleteRecord(string binId) =>
        _db.ExecuteNonQuery("delete from PLCtable where 库位ID=@id",
            new Dictionary<string, object?> { ["id"] = binId });

    /// <summary>按库位查（资料获取）。无数据返回 null。</summary>
    public DataRow? GetByBinId(string binId) =>
        _db.ExecuteDataRow("select * from PLCtable where 库位ID=@id",
            new Dictionary<string, object?> { ["id"] = binId });

    /// <summary>统计用全量行（Time,产品ID）。</summary>
    public DataTable LoadStatsRows() =>
        _db.ExecuteDataTable("SELECT Time,产品ID FROM PLCtable", null);

    /// <summary>
    /// 纯统计（老项目 UpdateStats 内核，提出来可测）。
    /// rows：(进入时间字符串, 产品ID⚫拼接串)；curingSeconds：当前固化秒；
    /// now：当前时间（测试可注入固定值）。
    /// </summary>
    public static CureStats ComputeStats(
        IEnumerable<(string EntryTime, string ProductIds)> rows,
        int curingSeconds, DateTime now)
    {
        var s = new CureStats();
        DateTime tomorrow = now.Date.AddDays(1);

        foreach (var (entryTimeStr, productText) in rows)
        {
            if (!DateTime.TryParse(entryTimeStr, out DateTime entry))
                continue;
            DateTime finish = entry.AddSeconds(curingSeconds);
            string[] splits = (productText ?? string.Empty).Split('⚫');
            if (now >= finish)
            {
                foreach (string p in splits)
                {
                    if (string.IsNullOrEmpty(p))
                        continue;
                    if (IsLeft(p)) s.DoneL++; else s.DoneR++;
                    s.Total++;
                }
            }
            else
            {
                foreach (string p in splits)
                {
                    if (string.IsNullOrEmpty(p))
                        continue;
                    if (IsLeft(p)) s.RestingL++; else s.RestingR++;
                    s.Total++;
                }
                if (finish.Day == tomorrow.Day && finish.Hour <= 8)
                {
                    foreach (string p in splits)
                    {
                        if (string.IsNullOrEmpty(p))
                            continue;
                        if (IsLeft(p)) s.TomorrowL++; else s.TomorrowR++;
                    }
                }
            }
        }
        return s;
    }

    /// <summary>左右分区：产品ID第4字符 L=左，其余=右（含短 ID，老项目会抛，此处归右）。</summary>
    internal static bool IsLeft(string productId) =>
        productId.Length >= 4 && productId[3] == 'L';
}
