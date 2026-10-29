// =========================================================================
// 账号安全测试：PBKDF2 哈希 + users 表账号体系 + DPAPI 记住密码。
//
// 全部用临时 db/内存，不碰现场库；测试密码全是专用串，真实初始密码只在
// App 启动播种处出现一次（上线前必须修改，文档不记录明文）。
// PBKDF2 12 万次迭代单次约 100ms，用例数克制，总耗时约 2s。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Security;

namespace CureLine.Tests;

public class SecurityTests : IDisposable
{
    private readonly string _tmp;

    public SecurityTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "PlcRdSec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private UserAccountStore NewStore(string? name = null)
    {
        var store = new UserAccountStore(
            new SqliteHelper(Path.Combine(_tmp, name ?? "s.db3")));
        store.EnsureSeeded("test-admin-pwd", "test-dev-pwd");
        return store;
    }

    [Fact(DisplayName = "同密码每次盐不同哈希不同")]
    public void Hasher_Salts_AreUnique()
    {
        var (h1, s1) = PasswordHasher.CreateHash("same-pwd");
        var (h2, s2) = PasswordHasher.CreateHash("same-pwd");
        Assert.False(Convert.ToBase64String(s1) == Convert.ToBase64String(s2));
        Assert.False(Convert.ToBase64String(h1) == Convert.ToBase64String(h2));
    }

    [Fact(DisplayName = "哈希验对过验错拦篡改拦")]
    public void Hasher_Verify_RoundTrip()
    {
        var (hash, salt) = PasswordHasher.CreateHash("test-pwd-1");
        Assert.True(PasswordHasher.Verify("test-pwd-1", hash, salt, PasswordHasher.Iterations));
        Assert.False(PasswordHasher.Verify("wrong-pwd", hash, salt, PasswordHasher.Iterations));
        // 篡改一字节即拦
        hash[0] ^= 0xFF;
        Assert.False(PasswordHasher.Verify("test-pwd-1", hash, salt, PasswordHasher.Iterations));
        Assert.False(PasswordHasher.Verify(null, hash, salt, PasswordHasher.Iterations));
        Assert.False(PasswordHasher.Verify("test-pwd-1", null, salt, PasswordHasher.Iterations));
    }

    [Fact(DisplayName = "新密码规则空短同旧全拦")]
    public void Hasher_PasswordRule_BlocksAll()
    {
        Assert.NotNull(PasswordHasher.ValidateNewPassword("", "old-pwd"));
        Assert.NotNull(PasswordHasher.ValidateNewPassword("12345", "old-pwd"));
        Assert.NotNull(PasswordHasher.ValidateNewPassword("old-pwd", "old-pwd"));
        Assert.Null(PasswordHasher.ValidateNewPassword("new-ok-1", "old-pwd"));
    }

    [Fact(DisplayName = "播种后两账号各归其位")]
    public void Store_Seed_VerifiesBoth()
    {
        var store = NewStore();
        Assert.Equal(LoginRole.Admin, store.Verify("admin", "test-admin-pwd"));
        Assert.Equal(LoginRole.Dev, store.Verify("dev", "test-dev-pwd"));
        Assert.Equal(2, store.ListUsers().Count);
    }

    [Fact(DisplayName = "错密码未知用户大小写全拒不区分")]
    public void Store_Verify_RejectsAll()
    {
        var store = NewStore();
        Assert.Equal(LoginRole.None, store.Verify("admin", "wrong"));
        Assert.Equal(LoginRole.None, store.Verify("dev", "test-admin-pwd"));
        Assert.Equal(LoginRole.None, store.Verify("ghost", "test-admin-pwd"));
        Assert.Equal(LoginRole.None, store.Verify("", ""));
        Assert.Equal(LoginRole.None, store.Verify(null, null));
        Assert.Equal(LoginRole.None, store.Verify("ADMIN", "test-admin-pwd"));
        Assert.Equal(LoginRole.None, store.Verify("DEV", "test-dev-pwd"));
        // 用户名空格容忍（去空格后比对）
        Assert.Equal(LoginRole.Admin, store.Verify("  admin  ", "test-admin-pwd"));
    }

    [Fact(DisplayName = "库里无明文密码")]
    public void Store_NoPlaintextInDb()
    {
        string db = Path.Combine(_tmp, "p.db3");
        var store = new UserAccountStore(new SqliteHelper(db));
        store.EnsureSeeded("test-admin-pwd", "test-dev-pwd");
        // db 文件十六进制里搜不到密码明文（UTF-8/UTF-16 双查）
        byte[] raw = File.ReadAllBytes(db);
        string hex = Convert.ToHexString(raw);
        string probe8 = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("test-admin-pwd"));
        string probe16 = Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes("test-admin-pwd"));
        Assert.DoesNotContain(probe8, hex);
        Assert.DoesNotContain(probe16, hex);
    }

    [Fact(DisplayName = "改密码旧错拦规则拦成功旧失效新生效")]
    public void Store_ChangePassword_FullFlow()
    {
        var store = NewStore();
        Assert.Equal("旧密码不正确",
            store.ChangePassword("admin", "wrong", "new-pwd-1"));
        Assert.NotNull(store.ChangePassword("admin", "test-admin-pwd", "123"));
        Assert.Null(store.ChangePassword("admin", "test-admin-pwd", "new-pwd-1"));
        Assert.Equal(LoginRole.None, store.Verify("admin", "test-admin-pwd"));
        Assert.Equal(LoginRole.Admin, store.Verify("admin", "new-pwd-1"));
        // dev 不受影响
        Assert.Equal(LoginRole.Dev, store.Verify("dev", "test-dev-pwd"));
    }

    [Fact(DisplayName = "dev代改不要旧密码不存在拦")]
    public void Store_SetPassword_ProxyFlow()
    {
        var store = NewStore();
        Assert.Equal("用户不存在", store.SetPassword("ghost", "new-pwd-9"));
        Assert.NotNull(store.SetPassword("admin", "123"));
        Assert.Null(store.SetPassword("admin", "new-pwd-9"));
        Assert.Equal(LoginRole.Admin, store.Verify("admin", "new-pwd-9"));
    }

    [Fact(DisplayName = "二次播种不覆盖已改密码")]
    public void Store_Reseed_NeverOverwrites()
    {
        var store = NewStore("r.db3");
        Assert.Null(store.ChangePassword("admin", "test-admin-pwd", "new-pwd-1"));
        // 重启级重播（模拟 App 每次启动调 EnsureSeeded）
        store.EnsureSeeded("test-admin-pwd", "test-dev-pwd");
        Assert.Equal(LoginRole.Admin, store.Verify("admin", "new-pwd-1"));
        Assert.Equal(LoginRole.None, store.Verify("admin", "test-admin-pwd"));
    }

    [Fact(DisplayName = "DPAPI加解密往返坏串回空")]
    public void RememberMe_Protect_RoundTrip()
    {
        string? token = RememberMeProtector.Protect("test-pwd-1");
        // Windows 本机本用户必成功；非 Windows 返回 null 即跳过断言后半。
        if (token == null)
            return;
        Assert.NotEqual("test-pwd-1", token);
        Assert.Equal("test-pwd-1", RememberMeProtector.Unprotect(token));
        Assert.Null(RememberMeProtector.Unprotect("!!!not-base64!!!"));
        Assert.Null(RememberMeProtector.Unprotect(
            Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        Assert.Null(RememberMeProtector.Unprotect(null));
        Assert.Null(RememberMeProtector.Protect(""));
    }
}
