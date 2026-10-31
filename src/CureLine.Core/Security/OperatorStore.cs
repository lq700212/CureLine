// =========================================================================
// OperatorStore：程序①操作员账号库（SQLite operators 表，三级角色）
//
// 干什么：账号的建/删/验密/代改密（PBKDF2 复用 PasswordHasher）。
//   三级角色（V0.0.49 起，替代四级 LEVEL.1~4，用户拍板）：
//     Operator 操作员 —— 只能登录（工单基础字段+数量），无改密权；
//     Admin    管理员 —— 加工艺策略/IO测试/过账配置/演示开关/用户管理，
//                       用户管理窗建删操作员、重置 operator+admin 密码（含自己）；
//     Dev      开发者 —— 全权限（含点表配置），可删 dev 外任何账号、
//                       可重置所有人密码（含自己）。
// 预置三账号（空表播种，初始密码调用方 App.OnStartup 传入，本类不存明文）：
//   operator/123456、admin/123456、dev/dev123 —— 上线必改。
// 谁能干什么全部带 actor 参数收在 Store 层兜底（VM 层只做置灰/提示，
// 双层防线与工单 Flush 兜底同思路）；防锁死红线=Dev 账号永不可删
// （角色不开放创建、预置唯一，系统永远留最高权限钥匙）。
// 怎么改：新增操作 = Store 加方法（actor 兜底）+ VM 加命令 + 测试补用例。
// 旧库兜底：operators 表 level 列若存老四级文本（Level1~4），FromText
//   就地映射（L4→Admin、L1~3→Operator）——项目未上线无兼容包袱，纯让
//   老测试库不崩，勿据此新增任何"兼容老分级"逻辑。
// =========================================================================

using System.Data;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Security;

/// <summary>操作员角色（三级：Operator 操作员 &lt; Admin 管理员 &lt; Dev 开发者）。</summary>
public enum OperatorRole
{
    Operator,
    Admin,
    Dev,
}

/// <summary>角色助手：存储文本 / 中文文案 / 门禁判断一处收敛。</summary>
public static class OperatorRoleInfo
{
    public static string ToText(OperatorRole role) => role switch
    {
        OperatorRole.Operator => "Operator",
        OperatorRole.Admin => "Admin",
        _ => "Dev",
    };

    public static OperatorRole FromText(string? text) => text?.Trim() switch
    {
        "Operator" => OperatorRole.Operator,
        "Admin" => OperatorRole.Admin,
        "Dev" => OperatorRole.Dev,
        // 旧四级库的身位兜底映射（见类头）：L4=管理员，L1~L3 一律降操作员（错降无害）。
        "Level4" => OperatorRole.Admin,
        "Level1" or "Level2" or "Level3" => OperatorRole.Operator,
        // 未知/脏文本按最低级处理（安全侧：错降无害，错升危险）
        _ => OperatorRole.Operator,
    };

    /// <summary>中文角色名（用户管理/登录顶栏/工单窗直接绑）。</summary>
    public static string DisplayName(OperatorRole role) => role switch
    {
        OperatorRole.Operator => "操作员",
        OperatorRole.Admin => "管理员",
        _ => "开发者",
    };

    /// <summary>门禁：操作者角色是否够得上 required（Dev ≥ Admin ≥ Operator）。</summary>
    public static bool AtLeast(OperatorRole? actual, OperatorRole required) =>
        actual.HasValue && actual.Value >= required;
}

/// <summary>账号行（用户管理窗列表 + 测试断言用）。</summary>
public sealed record OperatorAccount(string UserName, OperatorRole Role);

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
    /// 空表播种预置三账号（只播一次：表里有任一用户即跳过）。初始密码调用方
    /// （App 启动装配）传入，本类不存明文；上线必须改掉。
    /// 播种直写库走 SeedUser——AddUser 对 Dev 角色有"不开放创建"边界（那是给
    /// 登录用户用的兜底，不能拦预置装配），否则 dev 账号永远播不进库。
    /// </summary>
    public void EnsureSeeded(string operatorPassword, string adminPassword, string devPassword)
    {
        EnsureSchema();
        object? n = _db.ExecuteScalar("select count(*) from operators", null);
        if (n != null && Convert.ToInt64(n) > 0)
            return;
        SeedUser("operator", OperatorRole.Operator, operatorPassword);
        SeedUser("admin", OperatorRole.Admin, adminPassword);
        SeedUser("dev", OperatorRole.Dev, devPassword);
    }

    /// <summary>
    /// 登录校验。成功返回角色，失败/未知用户一律 null（不区分，防枚举）；
    /// 用户名去空格、大小写敏感（与主程序账号语义一致）。
    /// </summary>
    public OperatorRole? Verify(string? user, string? password)
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
        return OperatorRoleInfo.FromText(row["level"]?.ToString());
    }

    /// <summary>
    /// 账号是否在册（登录窗用户名下拉以外的场景用；被删账号不算在册）。
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

    /// <summary>全部账号（用户管理窗列表用；Dev>Admin>Operator 降序 + 用户名排序）。
    /// level 列存的是文本 Operator/Admin/Dev，字母序与权限序不一致——必须 case 显式定权，
    /// 直接 desc 会得到 Operator&gt;Dev&gt;Admin 的错序。</summary>
    public IReadOnlyList<OperatorAccount> ListUsers()
    {
        DataTable dt = _db.ExecuteDataTable(
            "select username, level from operators order by " +
            "case level when 'Dev' then 0 when 'Admin' then 1 else 2 end, username", null);
        var list = new List<OperatorAccount>(dt.Rows.Count);
        foreach (DataRow r in dt.Rows)
            list.Add(new OperatorAccount(
                r["username"]?.ToString() ?? "",
                OperatorRoleInfo.FromText(r["level"]?.ToString())));
        return list;
    }

    /// <summary>
    /// 新增账号（actor=当前登录者的角色，Store 层兜底）：
    /// ① 开发者账号不开放创建（预置唯一，防最高权限扩散）；
    /// ② 管理员只能创建操作员；
    /// ③ 重名/密码规则（≥6 位）不过返回中文原因。成功 null。
    /// </summary>
    public string? AddUser(string? user, OperatorRole role, string? password, OperatorRole actor)
    {
        if (role == OperatorRole.Dev)
            return "开发者账号不开放创建（预置唯一）";
        if (actor == OperatorRole.Admin && role == OperatorRole.Admin)
            return "管理员只能创建操作员账号";
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
        SeedUser(u, role, password!);
        return null;
    }

    /// <summary>
    /// 预置/新增共用落库（哈希+插入一处收敛）：EnsureSeeded 直播预置三账号、
    /// AddUser 过完权限链后也走此处写行。
    /// </summary>
    private void SeedUser(string user, OperatorRole role, string password)
    {
        var (hash, salt) = PasswordHasher.CreateHash(password);
        _db.ExecuteNonQuery(
            "insert into operators (username, level, pwd_hash, salt, iterations, updated_at)" +
            " values (@u, @l, @h, @s, @i, @t)",
            new Dictionary<string, object?>
            {
                ["u"] = user,
                ["l"] = OperatorRoleInfo.ToText(role),
                ["h"] = hash,
                ["s"] = salt,
                ["i"] = PasswordHasher.Iterations,
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
    }

    /// <summary>
    /// 删除账号（actor=当前登录者的角色，Store 层兜底）：
    /// ① 开发者账号永不可删（防锁死红线——系统永远留最高权限钥匙）；
    /// ② 管理员只能删操作员（管理员账号只有开发者可以删）。
    /// 返回中文原因，成功 null。
    /// </summary>
    public string? RemoveUser(string? user, OperatorRole actor)
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
        var target = OperatorRoleInfo.FromText(row?["level"]?.ToString());
        if (target == OperatorRole.Dev)
            return "开发者账号不可删除（防锁死）";
        if (actor == OperatorRole.Admin && target == OperatorRole.Admin)
            return "管理员账号只有开发者可以删除";
        _db.ExecuteNonQuery("delete from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        return null;
    }

    /// <summary>
    /// 代改密码（用户管理窗"重置密码"按编辑区新密码重置，不需要旧密码——
    /// operator 无改密权，改密全走此处）。actor=当前登录者的角色：
    /// ① 开发者账号的密码只有开发者本人可改；
    /// ② 管理员可重置操作员/管理员的密码（含自己）。
    /// 目标不存在返回"用户不存在"，规则不过返回规则原因，成功 null。
    /// </summary>
    public string? SetPassword(string? targetUser, string? newPassword, OperatorRole actor)
    {
        string u = targetUser?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return "用户名不能为空";
        object? n = _db.ExecuteScalar("select count(*) from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        if (n == null || Convert.ToInt64(n) == 0)
            return "用户不存在";
        DataRow? row = _db.ExecuteDataRow(
            "select level from operators where username=@u",
            new Dictionary<string, object?> { ["u"] = u });
        var target = OperatorRoleInfo.FromText(row?["level"]?.ToString());
        if (target == OperatorRole.Dev && actor != OperatorRole.Dev)
            return "开发者密码只能由开发者本人修改";
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
