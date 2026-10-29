// =========================================================================
// OperatorStore：程序①固化程控的四级操作员账号体系（operators 表）
//
// 干什么：CureStation 的登录验谁 + 权限管理窗的账号增删改——四级权限
//   LEVEL.1 操作员 / LEVEL.2 班长 / LEVEL.3 工程师 / LEVEL.4 管理员
//   （对照 docs/参考界面 权限管理页 HMI）。门禁缺省：工单页 L1+（途程/
//   数量/清零 L2+、制程代码 L3+）、工艺策略 L3+、点表/权限管理 L4+。
// 为什么自建表而不复用主程序 users 表：两程序分机部署（拆分设计），
//   账号是各程序私有资产——主程序 admin/dev 管网关配置，程序①四级管
//   现场操作；与 IDialogService"不下沉"同理。密码哈希复用 PasswordHasher
//   （PBKDF2 同款锁芯），表与 PLCtable 同库（备份拷一个 db3 全带走）。
// 怎么改：
//   - 表结构只加列不改列（username 主键 / level / pwd_hash / salt /
//     iterations / updated_at）；level 存 "Level1".."Level4" 文本。
//   - EnsureSeeded 只在空表播种（初始 LEVEL.4 admin，密码调用方传入，
//     本类不存明文常量）。
//   - 防锁死红线：删除/降级最后一个 LEVEL.4 一律拒绝——现场永远留一把
//     最高权限钥匙，否则账号配错谁也救不回来。
// =========================================================================

using System.Data;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Security;

/// <summary>操作员权限级别（对应参考 HMI 的 LEVEL.1~4，数字越大权限越高）。</summary>
public enum OperatorLevel
{
    Level1,
    Level2,
    Level3,
    Level4,
}

/// <summary>操作员级别助手：存储文本 / 中文文案 / 门禁判断一处收敛。</summary>
public static class OperatorLevelInfo
{
    public static string ToText(OperatorLevel level) => level switch
    {
        OperatorLevel.Level1 => "Level1",
        OperatorLevel.Level2 => "Level2",
        OperatorLevel.Level3 => "Level3",
        _ => "Level4",
    };

    public static OperatorLevel FromText(string? text) => text?.Trim() switch
    {
        "Level1" => OperatorLevel.Level1,
        "Level2" => OperatorLevel.Level2,
        "Level3" => OperatorLevel.Level3,
        "Level4" => OperatorLevel.Level4,
        // 未知/脏文本按最低级处理（安全侧：错降无害，错升危险）
        _ => OperatorLevel.Level1,
    };

    /// <summary>中文文案（界面直接绑）。</summary>
    public static string DisplayName(OperatorLevel level) => level switch
    {
        OperatorLevel.Level1 => "LEVEL.1 操作员",
        OperatorLevel.Level2 => "LEVEL.2 班长",
        OperatorLevel.Level3 => "LEVEL.3 工程师",
        _ => "LEVEL.4 管理员",
    };

    /// <summary>级别短名（不带 LEVEL 前缀，顶栏"管理员：admin"这类拼接用）。</summary>
    public static string ShortName(OperatorLevel level) => level switch
    {
        OperatorLevel.Level1 => "操作员",
        OperatorLevel.Level2 => "班长",
        OperatorLevel.Level3 => "工程师",
        _ => "管理员",
    };

    /// <summary>门禁：操作者级别是否够得上 required（L4 ≥ L3 ≥ L2 ≥ L1）。</summary>
    public static bool AtLeast(OperatorLevel? operatorLevel, OperatorLevel required) =>
        operatorLevel.HasValue && operatorLevel.Value >= required;
}

/// <summary>操作员账号行（权限管理窗列表 + 测试断言用）。</summary>
public sealed record OperatorAccount(string UserName, OperatorLevel Level);

public class OperatorStore
{
    private readonly SqliteHelper _db;

    public OperatorStore(SqliteHelper db)
    {
        _db = db;
    }

    /// <summary>默认库（现场库；测试用 TestScope 重定向 BaseDir 即隔离）。</summary>
    public static OperatorStore Default =>
        new(new SqliteHelper(AppPaths.DatabaseFile));

    /// <summary>建 operators 表（App 启动 EnsureDatabase 之后调，幂等）。</summary>
    public void EnsureSchema()
    {
        _db.ExecuteNonQuery(
            "CREATE TABLE IF NOT EXISTS operators (" +
            "username TEXT PRIMARY KEY, level TEXT NOT NULL, " +
            "pwd_hash BLOB NOT NULL, salt BLOB NOT NULL, " +
            "iterations INTEGER NOT NULL, updated_at TEXT NOT NULL)", null);
    }

    /// <summary>
    /// 空表播种预置管理员（只播一次：表里有任一用户即跳过）。初始密码调用方
    /// （App 启动装配）传入，本类不存明文；上线后必须改掉。
    /// </summary>
    public void EnsureSeeded(string adminPassword)
    {
        EnsureSchema();
        object? n = _db.ExecuteScalar("select count(*) from operators", null);
        if (n != null && Convert.ToInt64(n) > 0)
            return;
        AddUser("admin", OperatorLevel.Level4, adminPassword);
    }

    /// <summary>
    /// 登录校验。成功返回级别，失败/未知用户一律 null（不区分，防枚举）；
    /// 用户名去空格、大小写敏感（与主程序账号语义一致）。
    /// </summary>
    public OperatorLevel? Verify(string? user, string? password)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(password))
            return null;
        DataRow? row = _db.ExecuteDataRow(
            "select level, pwd_hash, salt, iterations from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (row == null)
            return null;
        byte[]? hash = row["pwd_hash"] as byte[];
        byte[]? salt = row["salt"] as byte[];
        int iterations = Convert.ToInt32(row["iterations"]);
        if (!PasswordHasher.Verify(password, hash, salt, iterations))
            return null;
        return OperatorLevelInfo.FromText(row["level"]?.ToString());
    }

    /// <summary>
    /// 账号是否在册（登录窗改密面板入口先拦"账号不存在"，admin 已删也算不存在）。
    /// 用户名去空格、忽略空串（空串一律不算在册）。
    /// </summary>
    public bool Exists(string? user)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return false;
        object? n = _db.ExecuteScalar(
            "select count(*) from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        return n != null && Convert.ToInt64(n) > 0;
    }

    /// <summary>全部账号（权限管理窗四级列表用；按级别降序 + 用户名排序）。</summary>
    public IReadOnlyList<OperatorAccount> ListUsers()
    {
        DataTable dt = _db.ExecuteDataTable(
            "select username, level from operators order by level desc, username", null);
        var list = new List<OperatorAccount>(dt.Rows.Count);
        foreach (DataRow r in dt.Rows)
            list.Add(new OperatorAccount(
                r["username"]?.ToString() ?? "",
                OperatorLevelInfo.FromText(r["level"]?.ToString())));
        return list;
    }

    /// <summary>
    /// 新增账号：重名/密码规则不过返回中文原因，成功 null。
    /// </summary>
    public string? AddUser(string? user, OperatorLevel level, string? password)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        object? n = _db.ExecuteScalar("select count(*) from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (n != null && Convert.ToInt64(n) > 0)
            return $"用户名 {u} 已存在";
        string? rule = PasswordHasher.ValidateNewPassword(password, null);
        if (rule != null)
            return rule;
        var (hash, salt) = PasswordHasher.CreateHash(password!);
        _db.ExecuteNonQuery(
            "insert into operators (username, level, pwd_hash, salt, iterations, updated_at)" +
            " values (@u, @l, @h, @s, @i, @t)",
            new Dictionary<string, object?>
            {
                ["u"] = u,
                ["l"] = OperatorLevelInfo.ToText(level),
                ["h"] = hash,
                ["s"] = salt,
                ["i"] = PasswordHasher.Iterations,
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        return null;
    }

    /// <summary>
    /// 删除账号。防锁死：最后一个 LEVEL.4 不许删（现场永远留一把最高权限钥匙）。
    /// 返回中文原因，成功 null。
    /// </summary>
    public string? RemoveUser(string? user)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        object? n = _db.ExecuteScalar(
            "select count(*) from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (n == null || Convert.ToInt64(n) == 0)
            return "用户不存在";
        DataRow? row = _db.ExecuteDataRow(
            "select level from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (row != null && OperatorLevelInfo.FromText(row["level"]?.ToString()) == OperatorLevel.Level4)
        {
            object? l4 = _db.ExecuteScalar(
                "select count(*) from operators where level=@l",
                new Dictionary<string, object?> { ["l"] = "Level4" });
            if (l4 != null && Convert.ToInt64(l4) <= 1)
                return "不能删除最后一个 LEVEL.4 账号（防锁死）";
        }
        _db.ExecuteNonQuery("delete from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        return null;
    }

    /// <summary>
    /// 调整账号级别。防锁死：把最后一个 LEVEL.4 降到 L3 以下拒绝。
    /// 返回中文原因，成功 null。
    /// </summary>
    public string? SetLevel(string? user, OperatorLevel level)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        object? n = _db.ExecuteScalar("select count(*) from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (n == null || Convert.ToInt64(n) == 0)
            return "用户不存在";
        DataRow? row = _db.ExecuteDataRow(
            "select level from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (row != null && level != OperatorLevel.Level4 &&
            OperatorLevelInfo.FromText(row["level"]?.ToString()) == OperatorLevel.Level4)
        {
            object? l4 = _db.ExecuteScalar(
                "select count(*) from operators where level=@l",
                new Dictionary<string, object?> { ["l"] = "Level4" });
            if (l4 != null && Convert.ToInt64(l4) <= 1)
                return "不能降级最后一个 LEVEL.4 账号（防锁死）";
        }
        _db.ExecuteNonQuery(
            "update operators set level=@l, updated_at=@t where username=@u",
            new Dictionary<string, object?>
            {
                ["l"] = OperatorLevelInfo.ToText(level),
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["u"] = u,
            });
        return null;
    }

    /// <summary>
    /// 改密码（权限管理窗管理员代改，不需要旧密码——调用方保证操作者够级别）。
    /// 目标不存在返回"用户不存在"，规则不过返回规则原因，成功 null。
    /// </summary>
    public string? SetPassword(string? targetUser, string? newPassword)
    {
        string u = targetUser?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        object? n = _db.ExecuteScalar("select count(*) from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (n == null || Convert.ToInt64(n) == 0)
            return "用户不存在";
        string? rule = PasswordHasher.ValidateNewPassword(newPassword, null);
        if (rule != null)
            return rule;
        var (hash, salt) = PasswordHasher.CreateHash(newPassword!);
        _db.ExecuteNonQuery(
            "update operators set pwd_hash=@h, salt=@s, iterations=@i, updated_at=@t" +
            " where username=@u",
            new Dictionary<string, object?>
            {
                ["h"] = hash,
                ["s"] = salt,
                ["i"] = PasswordHasher.Iterations,
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["u"] = u,
            });
        return null;
    }
}
