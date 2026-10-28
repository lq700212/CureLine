// =========================================================================
// RunModeSettings：菜单门禁判据 + 登录记忆的纯逻辑（供界面 + 测试共用）
//
// 干什么：为"登录/记住密码"提供验证与落盘 + 提供菜单项 gating 的角色判据。
//   历史：此类曾含 Single/Multi 运行模式切换（ParseMode/SaveMode 等），
//   已随单机链路删除（P2.1，git 历史可找回），类名沿用免大搬家。
// 为什么独立成类：WPF 层只做"事件→控件"搬运（项目约定），业务进 Core 可测；
//   读写走 ini 文件，纯函数方便用临时 ini 做单元测试，不碰现场 config。
// 怎么改：
//   - 登录验谁走 UserAccountStore（SQLite users 表 + PBKDF2，本类只做
//     菜单 gating 用的 IsDev/IsLoggedIn；各档能点什么由 WPF 菜单定）。
//     预置两档：admin（普通管理：可建快捷方式）、dev（最高权限：另含
//     PLC 配置入口）；用户名常量留这里，密码以任何形式都不进本类。
//   - 记住密码跟随账号：每个账号各记各的 DPAPI 密文（RememberToken_<账号>），
//     另存 SavedUser=最后一次登录名（供登录窗预填）；ini 里永无明文密码。
// =========================================================================

namespace PlcMesBridge.Core.Infrastructure;

/// <summary>登录角色（WPF 菜单按此 gating：None=未登录啥也点不了）。</summary>
public enum LoginRole
{
    None,
    Admin,
    Dev,
}

public static class RunModeSettings
{
    /// <summary>普通管理账号名（密码存 users 表 PBKDF2 哈希，不在此类）。</summary>
    public const string AdminUser = "admin";

    /// <summary>最高权限账号名（能看到设置下全部选项，含 PLC 配置）。</summary>
    public const string DevUser = "dev";

    /// <summary>是否最高权限（含 dev 的菜单项：PLC 配置等，凭此放行）。</summary>
    public static bool IsDev(LoginRole role) => role == LoginRole.Dev;

    /// <summary>是否已登录（快捷方式等 admin 可用项，凭此放行）。</summary>
    public static bool IsLoggedIn(LoginRole role) => role != LoginRole.None;

    // ================= 登录记忆（记住密码，跟随账号，密文） =================
    // 存 config.ini [setting]：每个账号各记各的 DPAPI 密文——
    //   RememberToken_<账号>=Base64(密文)，另存 SavedUser=最后一次登录名。
    // 密文只有本机本用户能解（见 Security.RememberMeProtector），ini 里永无明文。
    // 加解密在本类内完成（引用 Security），WPF 拿到的是可用明文或空，
    //   不直接碰 token，防各窗体各写一套加解密。
    // 旧明文键（全局 RememberLogin/SavedPwd、RememberLogin_<账号>/SavedPwd_<账号>）
    //   已废弃：PurgeLegacySecrets 启动时清扫，防 ini 里留明文影子。

    /// <summary>读登录记忆（看最后登录账号；有密文则解密回密码，解不开按无记忆）。</summary>
    public static (bool Remember, string User, string Password) ReadSavedLogin(string iniPath)
    {
        string last = IniFile.ReadStr(iniPath, "setting", "SavedUser", "");
        if (string.IsNullOrEmpty(last))
            return (false, "", "");
        var per = ReadSavedLogin(iniPath, last);
        if (per.Remember)
            return per;
        // 指名账号没记住：返回"不记住 + 指名账号 + 空密码"（窗体预填用户名用）。
        return (false, last, "");
    }

    /// <summary>读指定账号的记忆（登录窗切用户名跟密码时调这个）。</summary>
    public static (bool Remember, string User, string Password) ReadSavedLogin(string iniPath, string? user)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return (false, "", "");
        string token = IniFile.ReadStr(iniPath, "setting", "RememberToken_" + u, "");
        if (string.IsNullOrEmpty(token))
            return (false, u, "");
        string? pwd = Security.RememberMeProtector.Unprotect(token);
        if (pwd == null)
            return (false, u, "");
        return (true, u, pwd);
    }

    /// <summary>
    /// 写登录记忆（按账号存密文 + 更新 SavedUser=本次账号；取消勾选清自己账号的 token）。
    /// password 传登录框里的明文（本方法内 DPAPI 加密；加密失败按不记住处理，
    /// 如非 Windows 调试机——本次不记住，不惊动用户）。
    /// </summary>
    public static void SaveLogin(string iniPath, bool remember, string? user, string? password)
    {
        string u = user?.Trim() ?? "";
        if (string.IsNullOrEmpty(u))
            return;
        IniFile.Write(iniPath, "setting", "SavedUser", u);
        if (remember)
        {
            string? token = Security.RememberMeProtector.Protect(password);
            if (string.IsNullOrEmpty(token))
                IniFile.Delete(iniPath, "setting", "RememberToken_" + u);
            else
                IniFile.Write(iniPath, "setting", "RememberToken_" + u, token);
        }
        else
        {
            IniFile.Delete(iniPath, "setting", "RememberToken_" + u);
        }
    }

    /// <summary>
    /// 清扫旧明文记忆键（全局 RememberLogin/SavedPwd、两账号后缀键）。
    /// 调时机：App 启动一次。账号只有两档预置，键名可枚举，无需扫 section。
    /// </summary>
    public static void PurgeLegacySecrets(string iniPath)
    {
        IniFile.Delete(iniPath, "setting", "RememberLogin");
        IniFile.Delete(iniPath, "setting", "SavedPwd");
        foreach (string u in new[] { AdminUser, DevUser })
        {
            IniFile.Delete(iniPath, "setting", "RememberLogin_" + u);
            IniFile.Delete(iniPath, "setting", "SavedPwd_" + u);
        }
    }
}
