// =========================================================================
// AppConfig 加载测试：PLCuse/IP/DataLength/URL 全字段 + 非法输入自愈
//
// 锁定的行为（现场 ini 手改错也不能让程序起不来）：
// ① PLCuse 短了补 "1"、多了截断、空格忽略；② IP 缺键 → "127.0.0.1"；
// ③ DataLength 非法/非正数 → 799 并回填文件（走查 B1：原来 int.Parse
//   直接抛，启动崩）；④ URL 缺键回填缺省。
// 历史：Mode 用例已随单机链路删除（P2.1）。
// 全部用临时 ini（TestScope），不碰现场 config。
// =========================================================================

using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;
using PlcMesBridge.Tests.Mocks;

namespace PlcMesBridge.Tests;

public class AppConfigTests
{
    [Fact(DisplayName = "PLCuse短了补1长了截断")]
    public void Load_PlcUse_ShortPads_LongTruncates()
    {
        using var scope = new TestScope();
        IniFile.Write(scope.IniPath(), "setting", "PLCuse", "1, 0");
        AppConfig.Load(scope.IniPath());
        Assert.Equal("1", AppConfig.PlcUse[0]);
        Assert.Equal("0", AppConfig.PlcUse[1]);
        Assert.Equal("1", AppConfig.PlcUse[2]); // 短了补 1
        Assert.Equal(8, AppConfig.PlcUse.Length);

        IniFile.Write(scope.IniPath(), "setting", "PLCuse", "0,0,0,0,0,0,0,0,0,0");
        AppConfig.Load(scope.IniPath());
        Assert.Equal(8, AppConfig.PlcUse.Length);
        Assert.All(AppConfig.PlcUse, v => Assert.Equal("0", v));
    }

    [Fact(DisplayName = "IP缺键回填127")]
    public void Load_MissingIp_SelfHealsLoopback()
    {
        using var scope = new TestScope();
        AppConfig.Load(scope.IniPath());
        Assert.All(AppConfig.PlcIps, ip => Assert.Equal("127.0.0.1", ip));
        // 自愈：文件里已补上
        Assert.Equal("127.0.0.1",
            IniFile.ReadStr(scope.IniPath(), "setting", "PLCIP2", ""));
    }

    [Fact(DisplayName = "DataLength非法不崩溃回填799")]
    public void Load_BadDataLength_Heals799_NoThrow()
    {
        using var scope = new TestScope();
        foreach (string bad in new[] { "abc", "", "0", "-5", "799.5" })
        {
            IniFile.Write(scope.IniPath(), "MES", "DataLength", bad);
            var ex = Record.Exception(() => AppConfig.Load(scope.IniPath()));
            Assert.Null(ex);
            Assert.Equal(799, MesConfig.DataLength);
            Assert.Equal("799",
                IniFile.ReadStr(scope.IniPath(), "MES", "DataLength", ""));
        }
    }

    [Fact(DisplayName = "DataLength合法照读")]
    public void Load_GoodDataLength_ReadsThrough()
    {
        using var scope = new TestScope();
        IniFile.Write(scope.IniPath(), "MES", "DataLength", "500");
        AppConfig.Load(scope.IniPath());
        Assert.Equal(500, MesConfig.DataLength);
    }

    [Fact(DisplayName = "URL与触发缺键回填缺省")]
    public void Load_Urls_SelfHeal()
    {
        using var scope = new TestScope();
        AppConfig.Load(scope.IniPath());
        Assert.StartsWith("http://", MesConfig.UrlApi0030);
        Assert.Equal("R17000", MesConfig.TriggerApi0030);
        Assert.Equal("R12000", MesConfig.TriggerApi0033);
        Assert.Equal("R17001", MesConfig.ApiAddresses[2].PlcData);
        Assert.Equal("R17900", MesConfig.ApiAddresses[2].MesData);
    }
}
