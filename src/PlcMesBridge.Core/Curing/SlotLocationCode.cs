// =========================================================================
// SlotLocationCode：客户库位编码（Date Matrix 格式）换算——架×层 ↔ 库位编码
// =========================================================================
// 为什么独立成类：主窗仓位卡要按客户给的"库位编码表"显示（与现场机台实景
// 编排一致），后续还会有"扫码绑定"工序（扫产品条码 ↔ 绑库位）——界面显示
// 与扫码绑定必须用同一套换算，收在 Core 可测试、可复用，防止两处口径分叉。
//
// 编码规则（客户提供《自然静态养护(24H)库位编码表》，格式 Date Matrix）：
//   机台 30 个固化架 = 10 站一排（列 A~J）× 上中下 3 层（行 1~3），
//   编码 = 列字母 + 两位数字，数字 = 列序 + 层序 - 1（对角编码）：
//     第一排（上层）：A01 B02 C03 D04 E05 F06 G07 H08 I09 J10
//     第二排（中层）：A02 B03 C04 D05 E06 F07 G08 H09 I10 J11
//     第三排（下层）：A03 B04 C05 D06 E07 F08 G09 H10 I11 J12
//   即：架 rack（1~10）对应列字母 A~J，层 level（1~3）参与数字偏移。
//
// 怎么改：现场若"第一排"与表的对应关系相反（如第一排实际是下层），
// 只需调整调用方传入的 level 语义或在 Encode 里换算，规则本体不动；
// 机台布局若不是 10×3，编码表作废，需客户重新给表——常量改这里即可。
// =========================================================================

namespace PlcMesBridge.Core.Curing;

/// <summary>库位编码换算器（纯静态、无状态——编码规则是客户约定，不是配置）。</summary>
public static class SlotLocationCode
{
    /// <summary>每排站数（列数，字母 A~J）——客户库位编码表定死 10 站一排。</summary>
    public const int RacksPerRow = 10;

    /// <summary>层数（上中下三排）——客户库位编码表定死 3 层。</summary>
    public const int Levels = 3;

    /// <summary>
    /// 架×层 → 库位编码（如 架1层1 → "A01"、架10层3 → "J12"）。
    /// 越界（点表绑了超出 10×3 的架/层）返回占位码 "R{rack}L{level}"——
    /// 仅界面可见、不参与扫码绑定；正常机台就是 10×3，不会触发。
    /// </summary>
    public static string Encode(int rack, int level)
    {
        if (rack >= 1 && rack <= RacksPerRow && level >= 1 && level <= Levels)
        {
            // Date Matrix 对角编码：数字 = 列序 + 层序 - 1（见文件头规则表）
            int number = rack + level - 1;
            return $"{(char)('A' + rack - 1)}{number:00}";
        }
        return $"R{rack:00}L{level:00}";
    }

    /// <summary>
    /// 库位编码 → 架×层（扫码绑定工序的预留入口：扫到的库位条码反查物理仓位）。
    /// 非法输入（空/长度≠3/列越界/数字非法/层越界）一律返回 false——
    /// 调用方按"未知库位"处理，绝不静默猜一个仓位。
    /// 字母大小写均可（扫码枪可能出小写），数字必须两位（表内口径 A01 非 A1）。
    /// </summary>
    public static bool TryDecode(string? code, out int rack, out int level)
    {
        rack = 0;
        level = 0;
        if (string.IsNullOrWhiteSpace(code) || code.Length != 3)
            return false;

        char col = char.ToUpperInvariant(code[0]);
        if (col < 'A' || col > 'A' + (RacksPerRow - 1))
            return false;
        if (!int.TryParse(code.AsSpan(1), out int number) || number < 1)
            return false;

        rack = col - 'A' + 1;
        level = number - rack + 1;      // 反解自：number = rack + level - 1
        return level >= 1 && level <= Levels;
    }
}
