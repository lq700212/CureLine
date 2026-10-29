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

using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

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

    [Fact(DisplayName = "分段偏移缺省30,60并自愈落盘")]
    public void Load_SegmentOffsets_Default_Heals()
    {
        using var scope = new TestScope();
        AppConfig.Load(scope.IniPath());
        Assert.Equal(new[] { 30, 60 }, MesConfig.DataSegmentOffsets);
        Assert.Equal("30,60",
            IniFile.ReadStr(scope.IniPath(), "MES", "DataSegmentOffsets", ""));
    }

    [Fact(DisplayName = "分段偏移合法照读非法整体回缺省")]
    public void Load_SegmentOffsets_Custom_And_Invalid()
    {
        using var scope = new TestScope();
        IniFile.Write(scope.IniPath(), "MES", "DataSegmentOffsets", "45,90");
        AppConfig.Load(scope.IniPath());
        Assert.Equal(new[] { 45, 90 }, MesConfig.DataSegmentOffsets);

        // 任一非法整体回缺省（不做部分生效，防现场配一半），并回写自愈。
        foreach (string bad in new[] { "abc", "30", "30,60,0", "30,-5", "30,65536", "30,60,70,80,90,100,110,120,130" })
        {
            IniFile.Write(scope.IniPath(), "MES", "DataSegmentOffsets", bad);
            var ex = Record.Exception(() => AppConfig.Load(scope.IniPath()));
            Assert.Null(ex);
            Assert.Equal(new[] { 30, 60 }, MesConfig.DataSegmentOffsets);
            Assert.Equal("30,60",
                IniFile.ReadStr(scope.IniPath(), "MES", "DataSegmentOffsets", ""));
        }
    }

    [Fact(DisplayName = "扫描周期缺省200合法照读越界钳回200")]
    public void Load_ScanIntervalMs_Default_Clamped()
    {
        using var scope = new TestScope();
        AppConfig.Load(scope.IniPath());
        Assert.Equal(200, AppConfig.ScanIntervalMs);
        Assert.Equal("200",
            IniFile.ReadStr(scope.IniPath(), "setting", "ScanIntervalMs", ""));

        IniFile.Write(scope.IniPath(), "setting", "ScanIntervalMs", "300");
        AppConfig.Load(scope.IniPath());
        Assert.Equal(300, AppConfig.ScanIntervalMs);

        foreach (string bad in new[] { "abc", "", "10", "9999", "-1", "200.5" })
        {
            IniFile.Write(scope.IniPath(), "setting", "ScanIntervalMs", bad);
            var ex = Record.Exception(() => AppConfig.Load(scope.IniPath()));
            Assert.Null(ex);
            Assert.Equal(200, AppConfig.ScanIntervalMs);
            Assert.Equal("200",
                IniFile.ReadStr(scope.IniPath(), "setting", "ScanIntervalMs", ""));
        }
    }
}
