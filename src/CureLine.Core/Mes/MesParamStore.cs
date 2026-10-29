// =========================================================================
// MesParamStore：MES 全局参数记忆（复刻自老项目 MESGlobalParams.vb）
//
// 干什么：16 个 MES 报文常用字段跨窗体共享 + 落盘 config\mes_params.json。
// 为什么：调试窗填一次下次还在；单机进站/出站上传直接取这里的值组报文。
// 兼容：缺字段自动默认（字典 ContainsKey 逐个取，老项目语义）；
//   文件损坏只记日志不抛（启动不能被一个参数文件卡死）。
// =========================================================================

using Newtonsoft.Json;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Mes;

public static class MesParamStore
{
    public static string RC_NO { get; set; } = "";
    public static string PT_NO { get; set; } = "";
    public static string MAC_NO { get; set; } = "";
    public static string MAC_LOC_NO { get; set; } = "";
    public static string PLANT { get; set; } = "";
    public static string PRA_CODE { get; set; } = "";
    public static string PRA_Value { get; set; } = "";
    public static string Collect_Datetime { get; set; } = "";
    public static string ACTION { get; set; } = "I";
    public static string BC_NO { get; set; } = "";
    public static string SP_MEMO { get; set; } = "";
    public static string EMP_NO { get; set; } = "";
    public static string TL_EL_NO { get; set; } = "";
    public static string IN_DATETIME { get; set; } = "";
    public static string CHECK_RESULT { get; set; } = "0";
    public static string BO_NUMBER { get; set; } = "";

    /// <summary>全部 16 字段名（调试窗动态表单 + 测试共用，改这里即全局生效）。</summary>
    public static readonly (string Field, string Desc)[] AllFields =
    {
        ("RC_NO", "途程单号"), ("PT_NO", "制程代码"),
        ("MAC_NO", "机台编号"), ("MAC_LOC_NO", "机台位置号"),
        ("PLANT", "厂别"), ("PRA_CODE", "参数/状态编码"),
        ("PRA_Value", "参数/状态值"), ("Collect_Datetime", "采集时间"),
        ("ACTION", "进出标志(I/O)"), ("BC_NO", "条码"),
        ("SP_MEMO", "员工工号(SP)"), ("EMP_NO", "员工工号(EMP)"),
        ("TL_EL_NO", "工具号"), ("IN_DATETIME", "进出/上传时间"),
        ("CHECK_RESULT", "检测结果(0/1)"), ("BO_NUMBER", "板号(LINK)"),
    };

    /// <summary>各接口所需字段（复刻自调试窗 cmbApiType_SelectedIndexChanged）。</summary>
    public static readonly Dictionary<string, string[]> RequiredFields = new()
    {
        ["API0027"] = new[] { "RC_NO", "PT_NO", "MAC_NO", "MAC_LOC_NO", "PRA_CODE", "PRA_Value", "Collect_Datetime", "PLANT" },
        ["API0028"] = new[] { "RC_NO", "PT_NO", "MAC_NO", "MAC_LOC_NO", "PRA_CODE", "PRA_Value", "PLANT" },
        ["API0030"] = new[] { "RC_NO", "ACTION", "BC_NO", "PT_NO", "SP_MEMO", "MAC_NO", "MAC_LOC_NO", "TL_EL_NO", "IN_DATETIME", "CHECK_RESULT", "PLANT" },
        ["API0031"] = new[] { "RC_NO", "BC_NO", "PT_NO", "EMP_NO", "MAC_NO", "MAC_LOC_NO", "BO_NUMBER", "PLANT" },
        ["API0032"] = new[] { "RC_NO", "BC_NO", "PT_NO", "EMP_NO", "MAC_NO", "MAC_LOC_NO", "IN_DATETIME", "PLANT" },
    };

    public static string Get(string field) => field switch
    {
        "RC_NO" => RC_NO, "PT_NO" => PT_NO, "MAC_NO" => MAC_NO,
        "MAC_LOC_NO" => MAC_LOC_NO, "PLANT" => PLANT, "PRA_CODE" => PRA_CODE,
        "PRA_Value" => PRA_Value, "Collect_Datetime" => Collect_Datetime,
        "ACTION" => ACTION, "BC_NO" => BC_NO, "SP_MEMO" => SP_MEMO,
        "EMP_NO" => EMP_NO, "TL_EL_NO" => TL_EL_NO, "IN_DATETIME" => IN_DATETIME,
        "CHECK_RESULT" => CHECK_RESULT, "BO_NUMBER" => BO_NUMBER,
        _ => string.Empty,
    };

    public static void Set(string field, string value)
    {
        switch (field)
        {
            case "RC_NO": RC_NO = value; break;
            case "PT_NO": PT_NO = value; break;
            case "MAC_NO": MAC_NO = value; break;
            case "MAC_LOC_NO": MAC_LOC_NO = value; break;
            case "PLANT": PLANT = value; break;
            case "PRA_CODE": PRA_CODE = value; break;
            case "PRA_Value": PRA_Value = value; break;
            case "Collect_Datetime": Collect_Datetime = value; break;
            case "ACTION": ACTION = value; break;
            case "BC_NO": BC_NO = value; break;
            case "SP_MEMO": SP_MEMO = value; break;
            case "EMP_NO": EMP_NO = value; break;
            case "TL_EL_NO": TL_EL_NO = value; break;
            case "IN_DATETIME": IN_DATETIME = value; break;
            case "CHECK_RESULT": CHECK_RESULT = value; break;
            case "BO_NUMBER": BO_NUMBER = value; break;
        }
    }

    public static void LoadParams(string? filePath = null)
    {
        try
        {
            string path = filePath ?? AppPaths.MesParamsJson;
            if (!File.Exists(path))
                return;
            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (dict == null)
                return;
            foreach (var f in AllFields)
                if (dict.TryGetValue(f.Field, out string? v) && v != null)
                    Set(f.Field, v);
        }
        catch (Exception ex)
        {
            MesLogger.WriteLog("Config Error", "加载MES全局参数失败: " + ex.Message);
        }
    }

    public static void SaveParams(string? filePath = null)
    {
        try
        {
            string path = filePath ?? AppPaths.MesParamsJson;
            var dict = AllFields.ToDictionary(f => f.Field, f => Get(f.Field));
            string json = JsonConvert.SerializeObject(dict, Formatting.Indented);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MesLogger.WriteLog("Config Error", "保存MES全局参数失败: " + ex.Message);
        }
    }
}
