// =========================================================================
// 登录设置测试：菜单门禁常量 + 登录记忆（DPAPI 密文）。
// 全部用临时 ini，不碰现场 config；不调 AppConfig.Load（它会连带读参数文件）。
// 登录验谁搬到 UserAccountStoreTests（SQLite 账号体系），本文件只测记忆落盘。
// 历史：运行模式解析/落盘/重启判断用例已随单机链路删除（P2.1）。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureLine.Tests;

public class RunModeSettingsTests : IDisposable
{
    private readonly string _tmp;

    public RunModeSettingsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "PlcRdMode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact(DisplayName = "登录记忆缺键默认不记住")]
    public void SavedLogin_Missing_DefaultsNotRemember()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        var (remember, user, pwd) = RunModeSettings.ReadSavedLogin(ini);
        Assert.False(remember);
        Assert.Equal("", user);
        Assert.Equal("", pwd);
    }

    [Fact(DisplayName = "登录记忆勾选存取往返")]
    public void SavedLogin_Checked_RoundTrip()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        RunModeSettings.SaveLogin(ini, true, "admin", "test-pwd-1");
        var (remember, user, pwd) = RunModeSettings.ReadSavedLogin(ini);
        Assert.True(remember);
        Assert.Equal("admin", user);
        Assert.Equal("test-pwd-1", pwd);
    }

    [Fact(DisplayName = "登录记忆取消勾选清账号")]
    public void SavedLogin_Unchecked_ClearsSaved()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        RunModeSettings.SaveLogin(ini, true, "admin", "test-pwd-1");
        RunModeSettings.SaveLogin(ini, false, "admin", "test-pwd-1");
        var (remember, user, pwd) = RunModeSettings.ReadSavedLogin(ini);
        Assert.False(remember);
        // 最后登录名保留（供登录窗预填用户名），密码已清
        Assert.Equal("admin", user);
        Assert.Equal("", pwd);
        // 本账号 token 键已删（读回缺省空）
        Assert.Equal("", IniFile.ReadStr(ini, "setting", "RememberToken_admin", ""));
    }

    [Fact(DisplayName = "记住密码跟随账号各记各的")]
    public void SavedLogin_PerAccount_FollowsUser()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        RunModeSettings.SaveLogin(ini, true, "admin", "test-pwd-1");
        RunModeSettings.SaveLogin(ini, true, "dev", "test-pwd-2");
        // 各账号读到各的密码
        var (ra, ua, pa) = RunModeSettings.ReadSavedLogin(ini, "admin");
        Assert.True(ra);
        Assert.Equal("admin", ua);
        Assert.Equal("test-pwd-1", pa);
        var (rd, ud, pd) = RunModeSettings.ReadSavedLogin(ini, "dev");
        Assert.True(rd);
        Assert.Equal("dev", ud);
        Assert.Equal("test-pwd-2", pd);
        // 取消 admin 记住只清 admin，不影响 dev
        RunModeSettings.SaveLogin(ini, false, "admin", "test-pwd-1");
        var (ra2, _, pa2) = RunModeSettings.ReadSavedLogin(ini, "admin");
        Assert.False(ra2);
        Assert.Equal("", pa2);
        var (rd2, _, pd2) = RunModeSettings.ReadSavedLogin(ini, "dev");
        Assert.True(rd2);
        Assert.Equal("test-pwd-2", pd2);
    }

    [Fact(DisplayName = "ini里永无明文密码")]
    public void SavedLogin_NoPlaintextInIni()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        const string secret = "s3cret-pwd-xyz";
        RunModeSettings.SaveLogin(ini, true, "admin", secret);
        string raw = File.ReadAllText(ini);
        Assert.DoesNotContain(secret, raw);
    }

    [Fact(DisplayName = "旧明文记忆键清扫干净")]
    public void PurgeLegacySecrets_RemovesPlaintextKeys()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        // 手工摆旧格式残留（含明文）
        IniFile.Write(ini, "setting", "RememberLogin", "1");
        IniFile.Write(ini, "setting", "SavedPwd", "old-plain");
        IniFile.Write(ini, "setting", "RememberLogin_admin", "1");
        IniFile.Write(ini, "setting", "SavedPwd_admin", "old-plain");
        IniFile.Write(ini, "setting", "RememberLogin_dev", "1");
        IniFile.Write(ini, "setting", "SavedPwd_dev", "old-plain");
        // 新 token 不受影响
        RunModeSettings.SaveLogin(ini, true, "admin", "test-pwd-1");
        RunModeSettings.PurgeLegacySecrets(ini);
        Assert.Equal("0", IniFile.ReadStr(ini, "setting", "RememberLogin", "0"));
        Assert.Equal("", IniFile.ReadStr(ini, "setting", "SavedPwd", ""));
        Assert.Equal("", IniFile.ReadStr(ini, "setting", "RememberLogin_admin", ""));
        Assert.Equal("", IniFile.ReadStr(ini, "setting", "SavedPwd_admin", ""));
        Assert.Equal("", IniFile.ReadStr(ini, "setting", "RememberLogin_dev", ""));
        Assert.Equal("", IniFile.ReadStr(ini, "setting", "SavedPwd_dev", ""));
        string raw = File.ReadAllText(ini);
        Assert.DoesNotContain("old-plain", raw);
        // 新记忆还在
        var (remember, _, pwd) = RunModeSettings.ReadSavedLogin(ini, "admin");
        Assert.True(remember);
        Assert.Equal("test-pwd-1", pwd);
    }
}
