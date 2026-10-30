// =========================================================================
// 基础层测试：IniFile / SqliteHelper / AppPaths / LanguageService
// 全部用临时目录，不碰现场 config 与数据库。
// =========================================================================

using CureLine.Core.Data;
using CureLine.Core.Infrastructure;

namespace CureLine.Tests;

public class InfrastructureTests : IDisposable
{
    private readonly string _tmp;

    public InfrastructureTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "PlcRdTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact(DisplayName = "INI读不到回填缺省并返回缺省")]
    public void Ini_ReadMissing_WritesDefault_ReturnsDefault()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        string v = IniFile.ReadStr(ini, "setting", "Language", "CH");
        Assert.Equal("CH", v);
        // 回填进文件了（自愈语义）
        Assert.Equal("CH", IniFile.ReadStr(ini, "setting", "Language", "EN"));
    }

    [Fact(DisplayName = "INI读写往返")]
    public void Ini_WriteRead_RoundTrip()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        IniFile.Write(ini, "MES", "DataLength", "799");
        Assert.Equal("799", IniFile.ReadStr(ini, "MES", "DataLength", "0"));
        Assert.Equal(799, IniFile.ReadInt(ini, "MES", "DataLength", 0));
    }

    [Fact(DisplayName = "INI非法数字回填缺省")]
    public void Ini_BadNumber_ReturnsDefault()
    {
        string ini = Path.Combine(_tmp, "c.ini");
        IniFile.Write(ini, "s", "k", "abc");
        Assert.Equal(42, IniFile.ReadInt(ini, "s", "k", 42));
    }

    [Fact(DisplayName = "SQLite建库增删查往返")]
    public void Sqlite_Ensure_Insert_Query_Delete()
    {
        var db = new SqliteHelper(Path.Combine(_tmp, "t.db3"));
        db.EnsureDatabase();
        var store = new CureRecordStore(db);
        store.SaveRecord("KW1", "AAA⚫BBB", "---");
        var row = store.GetByBinId("KW1");
        Assert.NotNull(row);
        Assert.Equal("AAA⚫BBB", row["产品ID"]?.ToString());
        // 同库位覆盖（delete+insert 语义）
        store.SaveRecord("KW1", "CCC", "---");
        Assert.Equal("CCC", store.GetByBinId("KW1")!["产品ID"]?.ToString());
        Assert.Equal(1, store.DeleteRecord("KW1"));
        Assert.Null(store.GetByBinId("KW1"));
    }

    [Fact(DisplayName = "SQLite参数化防注入")]
    public void Sqlite_Param_NoInjection()
    {
        var db = new SqliteHelper(Path.Combine(_tmp, "t.db3"));
        db.EnsureDatabase();
        var store = new CureRecordStore(db);
        store.SaveRecord("KW1", "x", "---");
        // 恶意库位 ID 不能删掉整表
        store.DeleteRecord("KW1' OR '1'='1");
        Assert.NotNull(store.GetByBinId("KW1"));
    }

    [Fact(DisplayName = "中英切换词典")]
    public void Lang_Tr_Switch()
    {
        LanguageService.CurrentLanguage = "CH";
        Assert.Equal("连接机台成功", LanguageService.Tr("连接机台成功"));
        LanguageService.CurrentLanguage = "EN";
        Assert.Equal("Connect Station Success", LanguageService.Tr("连接机台成功"));
        Assert.Equal("未知词条原文", LanguageService.Tr("未知词条原文")); // 回退原文
        LanguageService.CurrentLanguage = "CH";
    }
}
