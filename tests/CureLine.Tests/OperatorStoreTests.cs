// =========================================================================
// OperatorStoreTests：程序①三级操作员账号体系（播种/验证/增删/代改密码/
// actor 权限兜底矩阵）。注意 PBKDF2 单次约 100ms——用例合并断言控制
// Verify 调用次数。预置种子：operator/123456、admin/123456、dev/dev123。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Security;

namespace CureLine.Tests;

public class OperatorStoreTests : IDisposable
{
    private readonly string _tmp;

    public OperatorStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "PlcBridgeOps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private OperatorStore NewStore()
    {
        var store = new OperatorStore(
            new SqliteHelper(Path.Combine(_tmp, "ops.db3")));
        store.EnsureSeeded("123456", "test-admin-pwd", "dev123");
        return store;
    }

    [Fact]
    public void Seed_Verify_RoundTrip_ThreeRoles()
    {
        var store = NewStore();
        Assert.Equal(OperatorRole.Operator, store.Verify("operator", "123456"));
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "test-admin-pwd"));
        Assert.Equal(OperatorRole.Dev, store.Verify("dev", "dev123"));
        Assert.Null(store.Verify("dev", "wrong-pwd"));     // 错密码
        Assert.Null(store.Verify("nobody", "dev123"));     // 未知用户不区分（防枚举）
        Assert.Null(store.Verify("", "x"));                // 空 input
        Assert.Null(store.Verify("dev", ""));              // 空密码拒
    }

    [Fact]
    public void Add_DuplicateWeakDevRoleAndAdminBoundary_Rejected()
    {
        var store = NewStore();
        // 开发者角色不开放创建（预置唯一，防最高权限扩散）——admin/dev 账号下都拒
        Assert.Contains("不开放创建", store.AddUser("d2", OperatorRole.Dev, "abc123", OperatorRole.Dev) ?? "");
        // 管理员只能建操作员
        Assert.Contains("只能创建操作员", store.AddUser("a2", OperatorRole.Admin, "abc123", OperatorRole.Admin) ?? "");
        // 重名 / 弱密码
        Assert.Contains("已存在", store.AddUser("admin", OperatorRole.Operator, "abc123", OperatorRole.Admin) ?? "");
        Assert.Contains("至少", store.AddUser("op1", OperatorRole.Operator, "123", OperatorRole.Admin) ?? "");
        // 管理员建操作员 OK；开发者建管理员 OK
        Assert.Null(store.AddUser("op1", OperatorRole.Operator, "abc123", OperatorRole.Admin));
        Assert.Null(store.AddUser("a2", OperatorRole.Admin, "abc123", OperatorRole.Dev));
        var users = store.ListUsers();
        Assert.Contains(users, u => u.UserName == "op1" && u.Role == OperatorRole.Operator);
        Assert.Contains(users, u => u.UserName == "a2" && u.Role == OperatorRole.Admin);
    }

    [Fact]
    public void Remove_DevBlockedAndAdminBoundary()
    {
        var store = NewStore();
        // 开发者账号谁都不能删（防锁死红线：dev 删 dev 也拒）
        Assert.Contains("不可删除", store.RemoveUser("dev", OperatorRole.Dev) ?? "");
        Assert.Contains("不可删除", store.RemoveUser("dev", OperatorRole.Admin) ?? "");
        Assert.Equal(OperatorRole.Dev, store.Verify("dev", "dev123")); // 还在

        // 管理员建了个管理员 → admin 删 admin 拒、dev 删 admin 放行
        Assert.Null(store.AddUser("a2", OperatorRole.Admin, "abc123", OperatorRole.Dev));
        Assert.Contains("只有开发者", store.RemoveUser("a2", OperatorRole.Admin) ?? "");
        Assert.Null(store.RemoveUser("a2", OperatorRole.Dev));
        Assert.Null(store.Verify("a2", "abc123")); // 已删

        // 管理员删操作员放行
        Assert.Null(store.AddUser("op1", OperatorRole.Operator, "abc123", OperatorRole.Dev));
        Assert.Null(store.RemoveUser("op1", OperatorRole.Admin));
        Assert.Null(store.Verify("op1", "abc123")); // 已删
    }

    [Fact]
    public void SetPassword_DevProtectedAndBoundary()
    {
        var store = NewStore();
        Assert.Contains("不存在", store.SetPassword("nobody", "abc123", OperatorRole.Dev) ?? "");
        // 管理员改开发者密码 → 拒（开发者密码只有开发者本人可改）
        Assert.Contains("开发者", store.SetPassword("dev", "new-dev-p1", OperatorRole.Admin) ?? "");
        Assert.Equal(OperatorRole.Dev, store.Verify("dev", "dev123")); // 旧密码仍有效
        // 开发者改自己密码 → 放行
        Assert.Null(store.SetPassword("dev", "new-dev-p1", OperatorRole.Dev));
        Assert.Equal(OperatorRole.Dev, store.Verify("dev", "new-dev-p1"));
        // 管理员重置操作员/管理员（含自己）→ 放行
        Assert.Null(store.SetPassword("operator", "new-op-p1", OperatorRole.Admin));
        Assert.Equal(OperatorRole.Operator, store.Verify("operator", "new-op-p1"));
        Assert.Null(store.SetPassword("admin", "new-pwd-1", OperatorRole.Admin));
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "new-pwd-1"));
        Assert.Null(store.Verify("admin", "test-admin-pwd")); // 旧密码失效
        // 弱密码规则照拦
        Assert.Contains("至少", store.SetPassword("operator", "123", OperatorRole.Admin) ?? "");
    }

    [Fact]
    public void RoleText_RoundTrip_LegacyMigrateAndUnknownFallsLow()
    {
        foreach (var role in new[] { OperatorRole.Operator, OperatorRole.Admin, OperatorRole.Dev })
            Assert.Equal(role, OperatorRoleInfo.FromText(OperatorRoleInfo.ToText(role)));
        // 旧四级库存量身位兜底映射：L4→管理员、L1~3→操作员（安全侧错降）
        Assert.Equal(OperatorRole.Admin, OperatorRoleInfo.FromText("Level4"));
        Assert.Equal(OperatorRole.Operator, OperatorRoleInfo.FromText("Level3"));
        Assert.Equal(OperatorRole.Operator, OperatorRoleInfo.FromText("Level2"));
        Assert.Equal(OperatorRole.Operator, OperatorRoleInfo.FromText("Level1"));
        // 脏文本按最低级处理（安全侧：错降无害，错升危险）
        Assert.Equal(OperatorRole.Operator, OperatorRoleInfo.FromText("垃圾数据"));
        // 中文文案
        Assert.Equal("操作员", OperatorRoleInfo.DisplayName(OperatorRole.Operator));
        Assert.Equal("管理员", OperatorRoleInfo.DisplayName(OperatorRole.Admin));
        Assert.Equal("开发者", OperatorRoleInfo.DisplayName(OperatorRole.Dev));
        // 门禁判断：Admin 够 Operator 不够 Dev；未登录一律不够
        Assert.True(OperatorRoleInfo.AtLeast(OperatorRole.Admin, OperatorRole.Operator));
        Assert.False(OperatorRoleInfo.AtLeast(OperatorRole.Admin, OperatorRole.Dev));
        Assert.False(OperatorRoleInfo.AtLeast(null, OperatorRole.Operator));
    }

    [Fact]
    public void Seed_LegacyLevel4Db_SelfHealsToThreeRoles()
    {
        // 现场实测坑（V0.0.49 收尾发现）：老库只播过 admin/Level4，EnsureSeeded
        // 空表才播——operator/dev 永远进不了库，dev/dev123 登录失败。自愈播种
        // 必须把这种老库拉回三账号（旧 Level% 行清掉+预置对号补齐）。
        var store = new OperatorStore(
            new SqliteHelper(Path.Combine(_tmp, "ops-legacy.db3")));
        var (hash, salt) = PasswordHasher.CreateHash("old-pwd");
        store.EnsureSchema();
        // 裸 insert 一行老口径（绕过 AddUser 边界模拟现场老库实况）
        var helper = new SqliteHelper(Path.Combine(_tmp, "ops-legacy.db3"));
        helper.ExecuteNonQuery(
            "insert into operators (username, level, pwd_hash, salt, iterations, updated_at)" +
            " values (@u, @l, @h, @s, @i, @t)",
            new Dictionary<string, object?>
            {
                ["u"] = "admin",
                ["l"] = "Level4",
                ["h"] = hash,
                ["s"] = salt,
                ["i"] = PasswordHasher.Iterations,
                ["t"] = "2026-01-01 00:00:00",
            });

        store.EnsureSeeded("123456", "123456", "dev123");

        // 三预置账号全在、角色对、密码对；老口径 admin 行已被清（旧密码失效）
        Assert.Equal(OperatorRole.Operator, store.Verify("operator", "123456"));
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "123456"));
        Assert.Equal(OperatorRole.Dev, store.Verify("dev", "dev123"));
        Assert.Null(store.Verify("admin", "old-pwd"));   // 老 Level4 残行已删
        Assert.Equal(3, store.ListUsers().Count);        // 只剩三预置账号
    }

    [Fact]
    public void Seed_ExistingAccounts_NeverOverwritten()
    {
        // 自愈播种必须遵守"在册不动"——运行期改过的密码不能被启动重播回预置值。
        var store = NewStore();
        Assert.Null(store.SetPassword("admin", "new-pwd-9", OperatorRole.Dev));
        store.EnsureSeeded("123456", "777777", "000000"); // 再跑一次启动播种
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "new-pwd-9"));
        Assert.Null(store.Verify("admin", "777777"));     // 覆盖即失败
    }
}
