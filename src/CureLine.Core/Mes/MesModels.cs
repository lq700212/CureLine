// =========================================================================
// MesModels：MES 接口数据模型（复刻自 MES_Core.vb，对应 W68 文档）
//
// 单机版 5 个接口 + 多机版追加 API0033（不良明细）与两个 DATA 拆包结构，
// 融合版全收录。注意两处协议细节（调试窗/网关共用，改前必读）：
// ① API0027/0028 外层是数组 []，其余是对象 {}（W68 文档规定）；
// ② BaseResponse.STATUS 是字符串 "1"=成功/"0"=失败，不是数字。
// =========================================================================

namespace CureLine.Core.Mes;

public static class MesModels
{
    /// <summary>通用返回：STATUS "1"成功/"0"失败，DATA 可能是对象也可能是字符串。</summary>
    public class BaseResponse
    {
        public string? STATUS { get; set; }
        public string? MSG { get; set; }
        public object? DATA { get; set; }
    }

    /// <summary>API0031 返回 DATA 数组元素：BC_NO / CHECK_RESULT / REMARK。</summary>
    public class DataResponse31
    {
        public string? BC_NO { get; set; }
        public string? CHECK_RESULT { get; set; }
        public string? REMARK { get; set; }
    }

    /// <summary>API0030 返回 DATA 数组元素：OUT_RESULT / OUT_REMARK / OUT_NEXTOP。</summary>
    public class DataResponse30
    {
        public string? OUT_RESULT { get; set; }
        public string? OUT_REMARK { get; set; }
        public string? OUT_NEXTOP { get; set; }
    }

    /// <summary>API0027 生产参数上传（外层包数组）。</summary>
    public class Api0027Request
    {
        public string? RC_NO { get; set; }
        public string? PT_NO { get; set; }
        public string? MAC_NO { get; set; }
        public string? MAC_LOC_NO { get; set; }
        public string? PRA_CODE { get; set; }
        public string? PRA_Value { get; set; }
        public string? Collect_Datetime { get; set; }
        public string? PLANT { get; set; }
    }

    /// <summary>API0028 停机数据采集（外层包数组，无采集时间）。</summary>
    public class Api0028Request
    {
        public string? RC_NO { get; set; }
        public string? PT_NO { get; set; }
        public string? MAC_NO { get; set; }
        public string? MAC_LOC_NO { get; set; }
        public string? PRA_CODE { get; set; }
        public string? PRA_Value { get; set; }
        public string? PLANT { get; set; }
    }

    /// <summary>API0030 进出板（ACTION I 进 / O 出）。</summary>
    public class Api0030Request
    {
        public string? RC_NO { get; set; }
        public string? ACTION { get; set; }
        public string? BC_NO { get; set; }
        public string? PT_NO { get; set; }
        public string? SP_MEMO { get; set; }
        public string? MAC_NO { get; set; }
        public string? MAC_LOC_NO { get; set; }
        public string? TL_EL_NO { get; set; }
        public string? IN_DATETIME { get; set; }
        public string? CHECK_RESULT { get; set; }
        public string? PLANT { get; set; }
    }

    /// <summary>API0031 Link Panel。</summary>
    public class Api0031Request
    {
        public string? RC_NO { get; set; }
        public string? BC_NO { get; set; }
        public string? PT_NO { get; set; }
        public string? EMP_NO { get; set; }
        public string? MAC_NO { get; set; }
        public string? MAC_LOC_NO { get; set; }
        public string? BO_NUMBER { get; set; }
        public string? PLANT { get; set; }
    }

    /// <summary>API0032 上传物料。</summary>
    public class Api0032Request
    {
        public string? RC_NO { get; set; }
        public string? BC_NO { get; set; }
        public string? PT_NO { get; set; }
        public string? EMP_NO { get; set; }
        public string? MAC_NO { get; set; }
        public string? MAC_LOC_NO { get; set; }
        public string? IN_DATETIME { get; set; }
        public string? PLANT { get; set; }
    }

    /// <summary>API0033 不良明细（多机版新增，外层包数组）。</summary>
    public class Api0033Request
    {
        public string? RC_NO { get; set; }
        public string? BOARD_SN { get; set; }
        public string? TOP_BTM { get; set; }
        public string? LINE_NO { get; set; }
        public string? PT_NO { get; set; }
        public string? S_TEST_TIME { get; set; }
        public string? E_TEST_TIME { get; set; }
        public string? CHK_STATUS { get; set; }
        public string? DOT_LOC { get; set; }
        public string? BIN_LOC { get; set; }
        public string? IMG_LOC { get; set; }
        public string? ERR_NO { get; set; }
        public string? SCR_ACTUAL { get; set; }
        public string? SCR_STD { get; set; }
        public string? SCR_MAX { get; set; }
    }
}
