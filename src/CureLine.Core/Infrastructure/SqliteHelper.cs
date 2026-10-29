// =========================================================================
// SqliteHelper：SQLite 唯一出入口（复刻自老项目 SQLiteHalperObj.vb）
//
// 干什么：对 DataBase\PLCdatabase.db3 的全部读写经此走，短连接（每次 Using）。
// 为什么短连接：老项目多线程（Timer 轮询 + Task 后台上传）共用一个 SQLiteBlood
//   实例，SQLite 写锁是文件级的，短连接 + 30s 超时是最省心的并发策略。
// 怎么改：业务层只调 ExecuteDataTable / ExecuteNonQuery / ExecuteScalar；
//   新增查询一律用参数化（@xxx），禁止字符串拼接（老项目 delete/select 有拼接
//   残留，新代码不许再加，见 AGENTS.md 红线）。
//
// 原文件名 SQLiteHalperObj 拼写有误（Halper），新项目修正为 SqliteHelper。
// =========================================================================

using System.Data;
using System.Data.SQLite;

namespace CureLine.Core.Infrastructure;

public class SqliteHelper
{
    public string DbPath { get; set; }

    public SqliteHelper(string dbPath)
    {
        DbPath = dbPath;
    }

    public SqliteHelper() : this(Path.Combine(
        Directory.GetCurrentDirectory(), "DataBase", "PLCdatabase.db3"))
    {
    }

    /// <summary>指定库地址建连接（调用方 Using 释放，见各 Execute 方法）。</summary>
    public SQLiteConnection GetConnection() => new($"Data Source={DbPath}");

    private static void PrepareCommand(
        SQLiteCommand cmd, SQLiteConnection conn, string cmdText,
        IDictionary<string, object?>? data)
    {
        if (conn.State != ConnectionState.Open)
            conn.Open();
        cmd.Parameters.Clear();
        cmd.Connection = conn;
        cmd.CommandText = cmdText;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = 30;
        if (data is { Count: >= 1 })
        {
            foreach (var kv in data)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value);
        }
    }

    /// <summary>查询返回 DataSet。</summary>
    public DataSet ExecuteDataset(string cmdText, IDictionary<string, object?>? data)
    {
        var ds = new DataSet();
        using var conn = GetConnection();
        using var cmd = new SQLiteCommand();
        PrepareCommand(cmd, conn, cmdText, data);
        using var da = new SQLiteDataAdapter(cmd);
        da.Fill(ds);
        return ds;
    }

    /// <summary>查询返回 DataTable（业务层最常用）。</summary>
    public DataTable ExecuteDataTable(string cmdText, IDictionary<string, object?>? data)
    {
        var dt = new DataTable();
        using var conn = GetConnection();
        using var cmd = new SQLiteCommand();
        PrepareCommand(cmd, conn, cmdText, data);
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);
        return dt;
    }

    /// <summary>返回第一行，无数据返回 null。</summary>
    public DataRow? ExecuteDataRow(string cmdText, IDictionary<string, object?>? data)
    {
        DataSet ds = ExecuteDataset(cmdText, data);
        if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            return ds.Tables[0].Rows[0];
        return null;
    }

    /// <summary>执行增删改，返回受影响行数。</summary>
    public int ExecuteNonQuery(string cmdText, IDictionary<string, object?>? data)
    {
        using var conn = GetConnection();
        using var cmd = new SQLiteCommand();
        PrepareCommand(cmd, conn, cmdText, data);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 返回 Reader（连接随 Reader 关闭：CommandBehavior.CloseConnection）。
    /// 警告：调用方必须释放 Reader，否则连接泄漏。能用 DataTable 就别用它。
    /// </summary>
    public SQLiteDataReader ExecuteReader(string cmdText, IDictionary<string, object?>? data)
    {
        var cmd = new SQLiteCommand();
        var conn = GetConnection();
        try
        {
            PrepareCommand(cmd, conn, cmdText, data);
            return cmd.ExecuteReader(CommandBehavior.CloseConnection);
        }
        catch
        {
            conn.Close();
            cmd.Dispose();
            throw;
        }
    }

    /// <summary>返回第一行第一列。</summary>
    public object? ExecuteScalar(string cmdText, IDictionary<string, object?>? data)
    {
        using var conn = GetConnection();
        using var cmd = new SQLiteCommand();
        PrepareCommand(cmd, conn, cmdText, data);
        return cmd.ExecuteScalar();
    }

    /// <summary>分页查询（老项目保留，当前业务未用，复刻以备将来）。</summary>
    public DataSet ExecutePager(
        ref int recordCount, int pageIndex, int pageSize,
        string cmdText, string countText, IDictionary<string, object?>? data)
    {
        if (recordCount < 0)
            recordCount = int.Parse(ExecuteScalar(countText, data)!.ToString()!);
        var ds = new DataSet();
        using var conn = GetConnection();
        using var cmd = new SQLiteCommand();
        PrepareCommand(cmd, conn, cmdText, data);
        using var da = new SQLiteDataAdapter(cmd);
        da.Fill(ds, (pageIndex - 1) * pageSize, pageSize, "result");
        return ds;
    }

    /// <summary>
    /// VACUUM 整理数据库。SQLite 删数据只进自由列表不还给 OS，
    /// 大量删除后调一次回收空间（老项目 ResetDataBass，原名拼写保留语义）。
    /// </summary>
    public void Vacuum()
    {
        using var conn = GetConnection();
        using var cmd = new SQLiteCommand();
        if (conn.State != ConnectionState.Open)
            conn.Open();
        cmd.Parameters.Clear();
        cmd.Connection = conn;
        cmd.CommandText = "vacuum";
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = 30;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 建库：确保目录 + PLCtable 表存在。老项目靠现场预置 db3 文件，
    /// 新项目启动时自建（与 INI 自愈同一思想），现场删库也不怕。
    /// 表结构 1:1：Time / 库位ID / 产品ID / Mark。
    /// </summary>
    public void EnsureDatabase()
    {
        string? dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        ExecuteNonQuery(
            "CREATE TABLE IF NOT EXISTS PLCtable (" +
            "Time TEXT, 库位ID TEXT, 产品ID TEXT, Mark TEXT)", null);
    }
}
