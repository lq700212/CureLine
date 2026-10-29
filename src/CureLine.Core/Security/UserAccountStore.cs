// =========================================================================
// UserAccountStore：账号体系（users 表：用户名 + 角色 + 盐 + 哈希）
//
// 干什么：登录验谁 + 改密码 + dev 代改，是"真正安全"替换明文密码的核心。
// 为什么放 SQLite：项目已有 System.Data.SQLite.Core 官方包 + SqliteHelper
//   唯一出入口，users 表与 PLCtable 同库（现场只拷一个 db3 就全带走，
//   备份/恢复无新增文件）；全部参数化，遵守 SQL 参数化铁律。
// 怎么改：
//   - 表结构只加列不改列（username 主键 / role / pwd_hash / salt /
//     iterations / updated_at）；iterations 存表里，PBKDF2 参数升级后
//     老哈希照常验（见 PasswordHasher 类头）。
//   - EnsureSeeded 只在表空时播种（已改过的密码永不覆盖，否则每次启动
//     重置密码就是后门）；初始密码由调用方（App 启动装配）传入，
//     本类不存任何明文密码常量。
//   - 用户名去空格比对、大小写敏感（与旧门禁语义一致）；未知用户与密码错
//     同返回 None，不区分（防用户名枚举）。
// =========================================================================

using System.Data;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Security;

/// <summary>账号行（改密码窗下拉 + 测试断言用）。</summary>
public sealed record UserAccount(string UserName, LoginRole Role);

public class UserAccountStore
{
    private readonly SqliteHelper _db;

    public UserAccountStore(SqliteHelper db)
    {
        _db = db;
    }

    /// <summary>默认库（现场库；测试用 TestScope 重定向 BaseDir 即隔离）。</summary>
    public static UserAccountStore Default =>
        new(new SqliteHelper(AppPaths.DatabaseFile));

    /// <summary>建 users 表（App 启动 EnsureDatabase 之后调，幂等）。</summary>
    public void EnsureSchema()
    {
        _db.ExecuteNonQuery(
            "CREATE TABLE IF NOT EXISTS users (" +
            "username TEXT PRIMARY KEY, role TEXT NOT NULL, " +
            "pwd_hash BLOB NOT NULL, salt BLOB NOT NULL, " +
            "iterations INTEGER NOT NULL, updated_at TEXT NOT NULL)", null);
    }

    /// <summary>
    /// 空表播种预置账号（只播一次：表里有任一用户即跳过，已改密码永不覆盖）。
    /// 初始密码调用方传入（App.OnStartup），本类不存明文。
    /// </summary>
    public void EnsureSeeded(string adminPassword, string devPassword)
    {
        EnsureSchema();
        object? n = _db.ExecuteScalar("select count(*) from users", null);
        if (n != null && Convert.ToInt64(n) > 0)
            return;
        SeedUser(RunModeSettings.AdminUser, LoginRole.Admin, adminPassword);
        SeedUser(RunModeSettings.DevUser, LoginRole.Dev, devPassword);
    }

    /// <summary>
    /// 登录校验。成功返回角色，失败/未知用户一律 None（不区分，防枚举）；
    /// 用户名去空格、大小写敏感。
    /// </summary>
    public LoginRole Verify(string? user, string? password)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(password))
            return LoginRole.None;
        DataRow? row = _db.ExecuteDataRow(
            "select role, pwd_hash, salt, iterations from users where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (row == null)
            return LoginRole.None;
        byte[]? hash = row["pwd_hash"] as byte[];
        byte[]? salt = row["salt"] as byte[];
        int iterations = Convert.ToInt32(row["iterations"]);
        if (!PasswordHasher.Verify(password, hash, salt, iterations))
            return LoginRole.None;
        return string.Equals(row["role"]?.ToString(), "Dev", StringComparison.Ordinal)
            ? LoginRole.Dev : LoginRole.Admin;
    }

    /// <summary>
    /// 自己改自己密码（改密码窗）：旧密码不对返回"旧密码不正确"，
    /// 新密码规则不过返回规则原因，成功返回 null。
    /// </summary>
    public string? ChangePassword(string? user, string? oldPassword, string? newPassword)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        if (Verify(u, oldPassword) == LoginRole.None)
            return "旧密码不正确";
        string? rule = PasswordHasher.ValidateNewPassword(newPassword, oldPassword);
        if (rule != null)
            return rule;
        WriteHash(u, newPassword!);
        return null;
    }

    /// <summary>
    /// dev 代改他人密码（不需要旧密码；调用方保证操作者是 dev）。
    /// 目标不存在返回"用户不存在"，规则不过返回规则原因，成功 null。
    /// </summary>
    public string? SetPassword(string? targetUser, string? newPassword)
    {
        string u = targetUser?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        object? n = _db.ExecuteScalar("select count(*) from users where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (n == null || Convert.ToInt64(n) == 0)
            return "用户不存在";
        string? rule = PasswordHasher.ValidateNewPassword(newPassword, null);
        if (rule != null)
            return rule;
        WriteHash(u, newPassword!);
        return null;
    }

    /// <summary>全部账号（改密码窗 dev 下拉选人用；按用户名排序）。</summary>
    public IReadOnlyList<UserAccount> ListUsers()
    {
        DataTable dt = _db.ExecuteDataTable(
            "select username, role from users order by username", null);
        var list = new List<UserAccount>(dt.Rows.Count);
        foreach (DataRow r in dt.Rows)
        {
            string name = r["username"]?.ToString() ?? "";
            var role = string.Equals(r["role"]?.ToString(), "Dev", StringComparison.Ordinal)
                ? LoginRole.Dev : LoginRole.Admin;
            list.Add(new UserAccount(name, role));
        }
        return list;
    }

    private void SeedUser(string user, LoginRole role, string password)
    {
        var (hash, salt) = PasswordHasher.CreateHash(password);
        _db.ExecuteNonQuery(
            "insert into users (username, role, pwd_hash, salt, iterations, updated_at)" +
            " values (@u, @r, @h, @s, @i, @t)",
            new Dictionary<string, object?>
            {
                ["u"] = user,
                ["r"] = role == LoginRole.Dev ? "Dev" : "Admin",
                ["h"] = hash,
                ["s"] = salt,
                ["i"] = PasswordHasher.Iterations,
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
    }

    private void WriteHash(string user, string password)
    {
        var (hash, salt) = PasswordHasher.CreateHash(password);
        _db.ExecuteNonQuery(
            "update users set pwd_hash=@h, salt=@s, iterations=@i, updated_at=@t" +
            " where username=@u",
            new Dictionary<string, object?>
            {
                ["h"] = hash,
                ["s"] = salt,
                ["i"] = PasswordHasher.Iterations,
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["u"] = user,
            });
    }
}
