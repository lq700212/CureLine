// =========================================================================
// INI 数字自愈测试：锁定"写进去的值下次还能读出来"（走查 B2）
//
// 背景：ReadInt/ReadByte 缺省原来按 "#0.000" 写（如 "799.000"），
// int.TryParse 解析不了——自愈一次没好，下次读还是失败。
// 已修为纯整数写；本文件锁定：坏值→缺省、自愈值→可再读。
// 纯内存 ini（TestScope 临时目录），不碰现场 config。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class IniNumberFormatTests
{
    [Fact(DisplayName = "ReadInt自愈值下次可读")]
    public void ReadInt_HealedValue_ParsesAgain()
    {
        using var scope = new TestScope();
        string ini = scope.IniPath();
        IniFile.Write(ini, "s", "k", "abc");
        Assert.Equal(42, IniFile.ReadInt(ini, "s", "k", 42));
        // 自愈写的是纯整数：下次按缺省 0 读，应读到 42 而不是 0
        Assert.Equal(42, IniFile.ReadInt(ini, "s", "k", 0));
    }

    [Fact(DisplayName = "ReadByte自愈值下次可读")]
    public void ReadByte_HealedValue_ParsesAgain()
    {
        using var scope = new TestScope();
        string ini = scope.IniPath();
        Assert.Equal(7, IniFile.ReadByte(ini, "s", "b", 7));
        Assert.Equal(7, IniFile.ReadByte(ini, "s", "b", 0));
    }

    [Fact(DisplayName = "ReadDouble保持三位小数且可读")]
    public void ReadDouble_HealedValue_ParsesAgain()
    {
        using var scope = new TestScope();
        string ini = scope.IniPath();
        IniFile.Write(ini, "s", "d", "not-a-number");
        Assert.Equal(1.5, IniFile.ReadDouble(ini, "s", "d", 1.5));
        Assert.Equal(1.5, IniFile.ReadDouble(ini, "s", "d", 0));
    }

    [Fact(DisplayName = "正常整数不受自愈影响")]
    public void ReadInt_GoodValue_Untouched()
    {
        using var scope = new TestScope();
        string ini = scope.IniPath();
        IniFile.Write(ini, "MES", "DataLength", "799");
        Assert.Equal(799, IniFile.ReadInt(ini, "MES", "DataLength", 0));
    }
}
