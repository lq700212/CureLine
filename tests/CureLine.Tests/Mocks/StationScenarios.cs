// =========================================================================
// StationScenarios：可复用的网关 mock 数据（一处改，全用例生效）
//
// 6 个接口的标准 JSON（字段按 MesModels 对齐 W68 文档），拿来就能
// PresetString 进 R 区触发全链路。
// 历史：原含单机固化收料种子（SeedSingleLoad/ProductAddr 等），已随单机
// 链路删除（P2.1，git 历史可找回）。
// =========================================================================

using Newtonsoft.Json;
using CureLine.Core.Mes;

namespace CureLine.Tests.Mocks;

public static class StationScenarios
{
    // ================= 网关 6 接口标准 JSON =================

    public static string Api0027Json(string rc = "RC1", string praValue = "300") =>
        JsonConvert.SerializeObject(new[]
        {
            new MesModels.Api0027Request
            {
                RC_NO = rc, PT_NO = "PT1", MAC_NO = "M1", MAC_LOC_NO = "L1",
                PRA_CODE = "CURE", PRA_Value = praValue,
                Collect_Datetime = "2026-09-21 10:00:00.000", PLANT = "P1",
            },
        });

    public static string Api0028Json(string rc = "RC1") =>
        JsonConvert.SerializeObject(new[]
        {
            new MesModels.Api0028Request
            {
                RC_NO = rc, PT_NO = "PT1", MAC_NO = "M1", MAC_LOC_NO = "L1",
                PRA_CODE = "DOWN", PRA_Value = "1", PLANT = "P1",
            },
        });

    public static string Api0030Json(string action = "I", string bc = "B1;") =>
        JsonConvert.SerializeObject(new MesModels.Api0030Request
        {
            RC_NO = "R1", ACTION = action, BC_NO = bc, PT_NO = "PT1",
            SP_MEMO = "SP1", MAC_NO = "M1", MAC_LOC_NO = "L1", TL_EL_NO = "T1",
            IN_DATETIME = "2026-09-21 10:00:00.000", CHECK_RESULT = "0", PLANT = "P1",
        });

    public static string Api0031Json(string bc = "B1") =>
        JsonConvert.SerializeObject(new MesModels.Api0031Request
        {
            RC_NO = "R1", BC_NO = bc, PT_NO = "PT1", EMP_NO = "E1",
            MAC_NO = "M1", MAC_LOC_NO = "L1", BO_NUMBER = "BO1", PLANT = "P1",
        });

    public static string Api0032Json(string bc = "B1") =>
        JsonConvert.SerializeObject(new MesModels.Api0032Request
        {
            RC_NO = "R1", BC_NO = bc, PT_NO = "PT1", EMP_NO = "E1",
            MAC_NO = "M1", MAC_LOC_NO = "L1",
            IN_DATETIME = "2026-09-21 10:00:00.000", PLANT = "P1",
        });

    public static string Api0033Json(string sn = "SN1") =>
        JsonConvert.SerializeObject(new[]
        {
            new MesModels.Api0033Request
            {
                RC_NO = "R1", BOARD_SN = sn, TOP_BTM = "TOP", LINE_NO = "L1",
                PT_NO = "PT1", S_TEST_TIME = "2026-09-21 10:00:00",
                E_TEST_TIME = "2026-09-21 10:01:00", CHK_STATUS = "1",
                DOT_LOC = "D1", BIN_LOC = "B1", IMG_LOC = "I1",
                ERR_NO = "E0", SCR_ACTUAL = "5", SCR_STD = "5", SCR_MAX = "6",
            },
        });

    // ================= MES 应答 =================

    /// <summary>成功应答（DATA 可为 JSON 数组串或 null）。</summary>
    public static string OkResponse(string msg = "ok", string dataJson = "null") =>
        "{\"STATUS\":\"1\",\"MSG\":\"" + msg + "\",\"DATA\":" + dataJson + "}";

    /// <summary>拒绝应答（STATUS=0）。</summary>
    public static string NgResponse(string msg = "no such barcode") =>
        "{\"STATUS\":\"0\",\"MSG\":\"" + msg + "\",\"DATA\":null}";

    /// <summary>0030 的 DATA 三段（OUT_RESULT/REMARK/NEXTOP）。</summary>
    public static string Data30(string result = "PASS", string remark = "rm", string next = "next") =>
        "[{\"OUT_RESULT\":\"" + result + "\",\"OUT_REMARK\":\"" + remark +
        "\",\"OUT_NEXTOP\":\"" + next + "\"}]";

    /// <summary>0031 的 DATA 三段（BC_NO/CHECK_RESULT/REMARK）。</summary>
    public static string Data31(string bc = "B1", string check = "1", string remark = "rm") =>
        "[{\"BC_NO\":\"" + bc + "\",\"CHECK_RESULT\":\"" + check +
        "\",\"REMARK\":\"" + remark + "\"}]";
}
