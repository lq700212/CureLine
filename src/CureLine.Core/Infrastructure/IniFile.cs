// =========================================================================
// IniFile：INI 配置唯一出入口（复刻自老项目 INIer.vb，行为 1:1）
//
// 干什么：读写 config\config.ini，kernel32 原生 API（Unicode 版）。
// 为什么这么设计：老项目约定"读不到就回填缺省再返回缺省"，保证 ini 自愈——
//   新装/删配置后第一次启动自动补全，不用手工建文件。复刻必须保留该语义，
//   否则缺键时调用方拿到空字符串会走错分支（如寄存器地址为空）。
// 怎么改：新增配置键时直接调 ReadStr/ReadInt，缺省值写在调用处（与老项目一致）。
// 注意：kernel32 P/Invoke 仅 Windows 可用，本项目 net8.0-windows，无跨平台问题。
// =========================================================================

using System.Runtime.InteropServices;
using System.Text;

namespace CureLine.Core.Infrastructure;

public static class IniFile
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPrivateProfileString(
        string lpAppName, string lpKeyName, string lpDefault,
        StringBuilder lpReturnedString, int nSize, string lpFileName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WritePrivateProfileString(
        string lpAppName, string? lpKeyName, string? lpString, string lpFileName);

    /// <summary>
    /// 读字符串。键不存在时把 defaultValue 回填进文件再返回（自愈语义，见类注释）。
    /// </summary>
    public static string ReadStr(string iniPath, string section, string key, string defaultValue)
    {
        var sb = new StringBuilder(1024);
        int n = GetPrivateProfileString(section, key, string.Empty, sb, sb.Capacity, iniPath);
        if (n > 0)
            return sb.ToString(0, n);
        Write(iniPath, section, key, defaultValue);
        return defaultValue;
    }

    /// <summary>读双精度。非数字同样回填缺省（老项目用 "#0.000" 格式，原样保留）。</summary>
    public static double ReadDouble(string iniPath, string section, string key, double defaultValue)
    {
        var sb = new StringBuilder(1024);
        int n = GetPrivateProfileString(section, key, string.Empty, sb, sb.Capacity, iniPath);
        if (n > 0 && double.TryParse(sb.ToString(0, n), out double v))
            return v;
        Write(iniPath, section, key, defaultValue.ToString("#0.000"));
        return defaultValue;
    }

    /// <summary>读字节（老项目 ReadByt 语义：按整数解析再转 byte）。</summary>
    public static byte ReadByte(string iniPath, string section, string key, long defaultValue)
    {
        var sb = new StringBuilder(1024);
        int n = GetPrivateProfileString(section, key, string.Empty, sb, sb.Capacity, iniPath);
        if (n > 0 && int.TryParse(sb.ToString(0, n), out int v))
            return (byte)v;
        // 注意：缺省必须写纯整数文本。老项目用 "#0.000" 格式（如 "799.000"），
        // int.TryParse 解析不了，下次读还是失败——此处改写纯整数，保证自愈一次就好。
        Write(iniPath, section, key, defaultValue.ToString());
        return (byte)defaultValue;
    }

    /// <summary>读整数（缺省/非法时回填，老项目同名 ReadInt 语义）。</summary>
    public static int ReadInt(string iniPath, string section, string key, long defaultValue)
    {
        var sb = new StringBuilder(1024);
        int n = GetPrivateProfileString(section, key, string.Empty, sb, sb.Capacity, iniPath);
        if (n > 0 && int.TryParse(sb.ToString(0, n), out int v))
            return v;
        // 同 ReadByte：缺省写纯整数（不要 "#0.000"），否则自愈的值下次还解析失败。
        Write(iniPath, section, key, defaultValue.ToString());
        return (int)defaultValue;
    }

    /// <summary>写键值。老项目返回空串，保持签名兼容（调用方可忽略返回值）。</summary>
    public static string Write(string iniPath, string section, string key, string value)
    {
        WritePrivateProfileString(section, key, value, iniPath);
        return string.Empty;
    }

    /// <summary>删键（传 null 即删除，老项目 INIDelete 语义）。</summary>
    public static string Delete(string iniPath, string section, string key)
    {
        WritePrivateProfileString(section, key, null, iniPath);
        return string.Empty;
    }
}
