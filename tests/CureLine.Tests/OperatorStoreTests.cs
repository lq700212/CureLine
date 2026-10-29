// =========================================================================
// OperatorStoreTests：程序①四级操作员账号体系（播种/验证/增删改/防锁死）
// 注意 PBKDF2 单次约 100ms——用例合并断言控制 Verify 调用次数。
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
        store.EnsureSeeded("test-admin-pwd");
        return store;
    }

    [Fact]
    public void Seed_Verify_RoundTrip()
    {
        var store = NewStore();
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "test-admin-pwd"));
        Assert.Null(store.Verify("admin", "wrong-pwd")); // 错密码
        Assert.Null(store.Verify("nobody", "test-admin-pwd")); // 未知用户不区分（防枚举）
        Assert.Null(store.Verify("", "x")); // 空 input
    }

    [Fact]
    public void Add_DuplicateAndWeak_Rejected()
    {
        var store = NewStore();
        Assert.Contains("已存在", store.AddUser("admin", OperatorLevel.Level2, "abc123") ?? "");
        Assert.Contains("至少", store.AddUser("op1", OperatorLevel.Level1, "123") ?? ""); // <6 位
        Assert.Null(store.AddUser("op1", OperatorLevel.Level1, "abc123"));
        var users = store.ListUsers();
        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.UserName == "op1" && u.Level == OperatorLevel.Level1);
    }

    [Fact]
    public void Remove_LastLevel4_Blocked()
    {
        var store = NewStore();
        // 只有一个 L4（admin）→ 删它 = 防锁死拒绝
        Assert.Contains("防锁死", store.RemoveUser("admin") ?? "");
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "test-admin-pwd")); // 还在

        // 再加一个 L4 → 删原 admin 放行
        Assert.Null(store.AddUser("boss", OperatorLevel.Level4, "abc123"));
        Assert.Null(store.RemoveUser("admin"));
        Assert.Null(store.Verify("admin", "test-admin-pwd")); // 已删
    }

    [Fact]
    public void SetLevel_DemoteLastLevel4_Blocked()
    {
        var store = NewStore();
        Assert.Contains("防锁死", store.SetLevel("admin", OperatorLevel.Level3) ?? "");
        // 加第二个 L4 后降级放行
        Assert.Null(store.AddUser("boss", OperatorLevel.Level4, "abc123"));
        Assert.Null(store.SetLevel("admin", OperatorLevel.Level3));
        Assert.Equal(OperatorLevel.Level3, store.Verify("admin", "test-admin-pwd"));
    }

    [Fact]
    public void SetPassword_AndUnknownUser_Errors()
    {
        var store = NewStore();
        Assert.Contains("不存在", store.SetPassword("nobody", "abc123") ?? "");
        Assert.Null(store.SetPassword("admin", "new-pwd-1"));
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "new-pwd-1"));
        Assert.Null(store.Verify("admin", "test-admin-pwd")); // 旧密码失效
    }

    [Fact]
    public void LevelText_RoundTrip_AndUnknownFallsLow()
    {
        foreach (var lv in new[] { OperatorLevel.Level1, OperatorLevel.Level2,
            OperatorLevel.Level3, OperatorLevel.Level4 })
            Assert.Equal(lv, OperatorLevelInfo.FromText(OperatorLevelInfo.ToText(lv)));
        // 脏文本按最低级处理（安全侧：错降无害，错升危险）
        Assert.Equal(OperatorLevel.Level1, OperatorLevelInfo.FromText("垃圾数据"));
        // 门禁判断：L3 够 L2 不够 L4
        Assert.True(OperatorLevelInfo.AtLeast(OperatorLevel.Level3, OperatorLevel.Level2));
        Assert.False(OperatorLevelInfo.AtLeast(OperatorLevel.Level3, OperatorLevel.Level4));
        Assert.False(OperatorLevelInfo.AtLeast(null, OperatorLevel.Level1)); // 未登录
    }
}
