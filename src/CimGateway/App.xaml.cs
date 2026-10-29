using System.IO;
using System.Windows;
using PlcMesBridge.Core.Data;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Security;

namespace CimGateway;

/// <summary>
/// 程序入口装配（对应老项目 Form1_Load 的前半：读配置→建库→种子账号→起主窗）。
/// 顺序：定基址 → 读 config.ini（语言/Mode/PLC/MES）→ 建 SQLite 库表（含 users）→
/// 播种预置账号（空表才播，已改密码永不覆盖）→ 清扫 ini 旧明文记忆键 → 起主窗。
/// config 缺键自动回填缺省（IniFile 自愈），现场删配置不怕。
/// 初始密码说明：只在此一处出现（播种用），上线前必须登录"设置→修改密码"改掉，
/// 文档不记录明文；users 表存 PBKDF2 哈希，拷走 db 也反推不出。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        // kernel32 写 INI 不会自动建目录（老项目靠现场预置 config/），此处确保。
        Directory.CreateDirectory(AppPaths.ConfigDir);
        AppConfig.Load();
        MesParamStoreLoad();
        new SqliteHelper(AppPaths.DatabaseFile).EnsureDatabase();
        // 预置账号播种（admin 普通管理，dev 最高权限；初始密码见上注释）。
        UserAccountStore.Default.EnsureSeeded("123456", "dev123");
        // 旧版本 ini 明文记忆键清扫（RememberLogin/SavedPwd 系，防明文影子）。
        RunModeSettings.PurgeLegacySecrets(AppPaths.ConfigIni);
    }

    private static void MesParamStoreLoad()
    {
        // AppConfig.Load 内已调 MesParamStore.LoadParams，此处占位说明装配链完整。
    }
}
