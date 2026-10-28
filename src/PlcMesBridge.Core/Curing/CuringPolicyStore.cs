// =========================================================================
// CuringPolicyStore：工艺策略库的读写与缺省策略（config/curingpolicy.json）
//
// 干什么：
//   ① Load/Save：策略库（多策略 + 激活名）JSON 读写（Newtonsoft、UTF-8 缩进）；
//      文件不存在/损坏 → 回 null 并带出自愈原因（对齐点表 Store 的自愈语义，
//      现场删了配置文件程序也不崩，策略窗重新保存即修复）。
//   ② BuildDefaultBook：缺省策略库——一个"标准固化"策略（全局 120 分钟、
//      门控关）并激活。首次启动/配置丢失时兜底，现场第一次开机就能跑。
//
// 怎么改：新增策略字段只动 CuringPolicy（JSON 自动跟随）；缺省时长如需
//   调整改 BuildDefaultBook 一处。消费方：CureStation 主窗（自愈 + 热切换）、
//   工艺策略窗（增删改/激活/保存）。
// =========================================================================

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using PlcMesBridge.Core.Infrastructure;

namespace PlcMesBridge.Core.Curing;

public static class CuringPolicyStore
{
    /// <summary>策略库文件路径：exe 同目录 config/curingpolicy.json。</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(AppPaths.ConfigDir, "curingpolicy.json");

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
    };

    /// <summary>
    /// 读策略库；文件不存在/空/JSON 损坏一律回 null（调用方兜缺省库并落盘），
    /// 原因文本进 reasons 供日志（启动期不弹框打扰操作员）。
    /// </summary>
    public static CuringPolicyBook? Load(string? path = null, List<string>? reasons = null)
    {
        path ??= DefaultPath;
        reasons?.Clear();
        try
        {
            if (!File.Exists(path))
            {
                reasons?.Add("工艺策略文件不存在（首次启动或被删除），使用缺省策略。");
                return null;
            }
            var book = JsonConvert.DeserializeObject<CuringPolicyBook>(
                File.ReadAllText(path), JsonSettings);
            if (book == null)
            {
                reasons?.Add("工艺策略文件内容为空，使用缺省策略。");
                return null;
            }
            return book;
        }
        catch (Exception ex)
        {
            // 配置坏了不 crash 现场程序——回 null 兜缺省，策略窗重新保存即修复。
            reasons?.Add($"工艺策略文件损坏（{ex.Message}），使用缺省策略。");
            return null;
        }
    }

    /// <summary>保存策略库（UTF-8 缩进 JSON）。目录不存在自动建（kernel32 不自动建目录同款前拦）。</summary>
    public static void Save(CuringPolicyBook book, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonConvert.SerializeObject(book, JsonSettings));
    }

    /// <summary>
    /// 缺省策略库：一个"标准固化"策略（全局 120 分钟、门控关）并激活。
    /// 120 分钟是骨架期占位值——现场工艺时长确认后在策略窗改（客户口径
    /// "固化几小时"待确认，见报价文档待确认项①）。
    /// </summary>
    public static CuringPolicyBook BuildDefaultBook() => new()
    {
        Policies =
        {
            new CuringPolicy { Name = "标准固化", GlobalMinutes = 120 },
        },
        ActiveName = "标准固化",
    };
}
