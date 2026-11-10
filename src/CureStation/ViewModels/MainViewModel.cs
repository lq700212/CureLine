// =========================================================================
// MainViewModel：固化程控主窗编排（程序①）
//
// 干什么：① 启动时读点表（config/pointtable.json）+ 工艺策略
//   （config/curingpolicy.json，缺则自愈缺省库）→ 造总线（Mock/真机按
//   config.ini [setting] Simulate 键）+ 仓位状态机后半台跑；
//   ② 500ms 拉快照刷仓位网格（状态机状态一并注入卡片）；
//   ③ 点表配置窗/工艺策略窗命令（保存后重建总线/刷新策略即生效）。
// 为什么 500ms 轮询 UI：状态机的灯写与计时全在后台（铁律 2），UI 只读
//   快照与状态机状态，500ms 全量刷最稳（事件驱动 UI 属后续优化）。
// 怎么改：新增窗体命令经 IDialogService；总线边沿经 _machine 转发（后台
//   线程直调，不碰 UI）；重连只换总线通道（Attach）不丢仓位流程。
// =========================================================================

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CureStation.Themes;
using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Core.Services;
using CureLine.ViewModels;
using Kaleidoscope.Security;
using LicenseState = Kaleidoscope.Security.LicenseState;

namespace CureStation.ViewModels;

/// <summary>仓位数据行：架×层的实时状态（卡片网格直接绑这些 bool/文本）。</summary>
public sealed class RackSlotViewModel : ViewModelBase
{
    public int Rack { get; set; }
    public int Level { get; set; }

    public string Title => $"架{Rack}-{LevelName(Level)}";

    /// <summary>
    /// 客户库位编码（Date Matrix 格式，如 A01/J12）——与现场机台实景编排一致，
    /// 是卡片主标题；后续扫码绑定工序以它为库位主键（换算逻辑收在
    /// Core 的 SlotLocationCode，界面显示与扫码绑定同源，防口径分叉）。
    /// 双架口径（V0.0.20）：按架内站号换算（全局架号 11~20 → 站 1~10 → A01~J12），
    /// 每台架各自贴同一套标签；单架（架 1~10）与原 Encode 完全等价。
    /// Rack/Level 构造后不变，计算属性无需 INPC。
    /// </summary>
    public string Code => SlotLocationCode.EncodeInRack(Rack, Level);

    private static string LevelName(int level) => level switch
    {
        1 => "上层", 2 => "中层", _ => "下层",
    };

    // 展示状态（bool 转中文文本，卡片尾部一行放"层名 + 托盘"）
    /// <summary>卡片尾部托盘文案："◈ 托盘在"（有货）/ "无托盘"（空位）/ "—"(读不到)。</summary>
    private string _trayFoot = "—";
    public string TrayFoot
    {
        get => _trayFoot;
        private set => Set(ref _trayFoot, value);
    }
    private string _grillText = "—";
    public string GrillText
    {
        get => _grillText;
        private set => Set(ref _grillText, value);
    }
    private string _lampText = "灭";
    public string LampText
    {
        get => _lampText;
        private set => Set(ref _lampText, value);
    }
    private string _slotStateText = "空闲";
    public string SlotStateText
    {
        get => _slotStateText;
        private set => Set(ref _slotStateText, value);
    }

    // ---------- 扫码绑定（P3.3 扫码工序） ----------
    // 卡片库位码正下方显示产品码（V0.0.80 从卡片 foot 底部上移——底缘太不醒目）；
    // 未绑定且在架时红色加粗"未绑定"；完整码在卡片 ToolTip 给全（V0.0.86 用户
    // 拍板，推翻 V0.0.82"ToolTip 不给全码"——卡上摘要可能头尾省略，悬停即看全，
    // 12 秒 ShowDuration 够扫码核对）。
    // 绑定数据由会话（SlotBindingSession）维护，每拍随 Refresh 注入。

    private string _productCode = "";
    /// <summary>本库位已绑定的产品条码（空=未绑定）。构造后由 Refresh 每拍注入。</summary>
    public string ProductCode
    {
        get => _productCode;
        private set => Set(ref _productCode, value);
    }

    private string _bindingText = "";
    /// <summary>库位码下方产品码文案：产品码（主题蓝）/ "未绑定"（红加粗，托盘在架却没扫码）/ 空（空位）。</summary>
    public string BindingText
    {
        get => _bindingText;
        private set => Set(ref _bindingText, value);
    }

    private bool _bindingMissing;
    /// <summary>托盘在架但库位未绑定产品（XAML 红字触发条件；流程不拦截，仅提示）。</summary>
    public bool BindingMissing
    {
        get => _bindingMissing;
        private set => Set(ref _bindingMissing, value);
    }

    private bool _hasBindingText;
    /// <summary>产品码行是否需要占位（无产品码且未报警=空位，直接隐藏不留空白）。</summary>
    public bool HasBindingText
    {
        get => _hasBindingText;
        private set => Set(ref _hasBindingText, value);
    }

    // ---------- SN 换行区分档（V0.0.84：两行装不下递增三行/四行，用户拍板） ----------
    // V0.0.83 是"固定两行区 + 摘要兜底"；V0.0.84 把摘要兜底的起征点后移，长码
    // 先按需要多占一两行、仍装不下才摘要。行数按显示文本长度分档（唯一口
    // SnZoneOf），随 Refresh 一拍同步落 Height 与 mid 补偿，防两者异步抖动。
    // ★V0.0.85 小屏自适应（用户拍板"尽量换行显示完整；重叠风险下优先保证
    // 不与固化时间文本重叠，SN 减行 + 中段 …"）：主窗实测卡片渲染高
    // （30 卡 UniformGrid 均分同高，量一张代表全部）→ MainViewModel 换算
    // 出 SN 最大行数档 SnMaxRows（1~4，缺省 4）统一下发 30 张卡——正常
    // 窗口档恒 4（行为与 V0.0.84 完全一致），卡矮时档位下降：字符摘要
    // 阈值随档联动（档×13）、被截时摘要保头尾（≥2 档"头6+…+尾10"、
    // 1 档"头4+…+尾6"），区高/补偿同拍收窄，mid 大数字再也不被挤。

    private double _snZoneHeight = 32;
    /// <summary>
    /// SN 换行区高度（MainWindow.xaml SN 区 Border.Height 直绑，FallbackValue=32）：
    /// 按最终显示文本长度分档——1~2 行恒 32（V0.0.83 两行区定标，短码与摘要码
    /// 锚点零移动）、3 行 48、4 行 64；行数按保守容量 13 字符/行估算、峰值四行。
    /// </summary>
    public double SnZoneHeight
    {
        get => _snZoneHeight;
        private set => Set(ref _snZoneHeight, value);
    }

    private System.Windows.Thickness _snZoneCompensation = new(0);
    /// <summary>
    /// mid 区补偿 Margin（MainWindow.xaml mid Grid.Margin 直绑）：无 SN 整区
    /// Collapsed 时 =0（与旧版像素一致）；有 SN 时 = (0,0,0, Margin-top 4 + 区高)
    /// ——区高 32/48/64 对应补偿 36/52/68，mid 大数字居中锚点始终与无 SN 卡一致。
    /// 用 Thickness 直绑替代 V0.0.83 的 Style+DataTrigger（补偿值不再恒 36，
    /// 需随行数变化，触发器表达不了动态值）。
    /// </summary>
    public System.Windows.Thickness SnZoneCompensation
    {
        get => _snZoneCompensation;
        private set => Set(ref _snZoneCompensation, value);
    }

    private int _snMaxRows = 4;
    /// <summary>
    /// SN 显示区最大行数档（V0.0.85 小屏自适应；1=单行 ~ 4=四行，缺省 4）。
    /// 正常窗口恒 4——分档行为与 V0.0.84（32/48/64）完全一致；主窗实测卡片
    /// 渲染高度不够时由 MainViewModel.UpdateSnCardHeight 统一下发降档
    /// （30 卡同高、同步降，用户拍板"重叠风险下优先固化时间，SN 减行 + 中段 …"）。
    /// setter 变样即重算 SN 展示（文案摘要阈值=档×13 + 区高/补偿同拍落，
    /// 不等下一拍 Refresh——窗口拖动中降档要立刻跟上）。
    /// </summary>
    public int SnMaxRows
    {
        get => _snMaxRows;
        set
        {
            var rows = Math.Clamp(value, 1, 4);
            if (Set(ref _snMaxRows, rows))
                RecomputeSnPresentation();
        }
    }

    // ---- 下列常量与换算是小屏自适应的唯一定标处（改过要同步注释推导链） ----
    private const double SnLineHeight = 16;    // 11 号字单行高（V0.0.83 两行区=32 定标）
    private const double SnZoneTopMargin = 4;  // SN 区 Margin-top（并入 mid 补偿口径）

    /// <summary>
    /// 卡内除 SN 外的必需高度（保守定标）：SlotCard 模板三段（MainWindow.xaml）
    /// ——库位码行 17 号 ≈23 + mid 大数字全套原生高 ≈70（数字 Viewbox MaxHeight 36
    /// + 副文 16+上距 3 + 进度条 6+上距 9）+ foot 行 ≈18 + 卡内边距上 10 下 8 = 18
    /// → ≈129，+6 渲染误差缓冲 = 135。取大只会**早降档**（宁紧勿松——不重叠是
    /// 底线，早降档代价只是底部少量留白），故仍故意略高估。
    /// </summary>
    private const double SnChromeHeight = 135;

    /// <summary>
    /// 实测卡高 → SN 最大行数档（V0.0.85 小屏自适应唯一换算口，
    /// MainViewModel.UpdateSnCardHeight 消费）。
    /// ★V0.0.85 二轮收口（用户实测"缩小窗口时 SN 与固化时间重叠"）：档 r 需要
    /// 卡高 = Chrome(135) + 2×SN 区总占高(4+16r)——SN 区在 Row0 占一份、mid 的
    /// Margin-bottom 补偿（=SN 区同值）在 Row1 **再占一份**（一轮换算漏计了
    /// 后者，档界被系统性低估 30~62px：缩小窗口中卡高必穿过溢出带，mid 大数字
    /// 溢出绘制进 SN 区 = 用户看到的重叠；XAML 已加 mid 整包 Viewbox 兜底，
    /// 双保险后本公式只影响"何时过早缩小 mid 内容"的档位精度）。解 r：
    /// r = floor((cardHeight − 135 − 8) / 32)，档界 **>271 → 4 档（最大化卡高
    /// ≈277 仍余 6px 全覆盖，行为与 V0.0.84 一致）/ >239 → 3 / >207 → 2 /
    /// >175 → 1 / 更低 → 1（钳底）**。
    /// </summary>
    public static int SnMaxRowsForCardHeight(double cardHeight)
    {
        // 每档步进 = 2×SnLineHeight = 32（margin 常数已由分子 −2×SnZoneTopMargin 扣掉；
        // 分母写成 2*(margin+line)=40 是一轮笔误——会整体多降半档）
        var rows = (int)Math.Floor((cardHeight - SnChromeHeight - 2 * SnZoneTopMargin)
                                   / (2 * SnLineHeight));
        return Math.Clamp(rows, 1, 4);
    }

    // ---------- 卡片视觉状态（Kind，新卡模板六态状态口径，2026-10-29） ----------
    // 六态键与主题字典 Brush.Tint*/Brush.SlotBorder*/Brush.CardGlow*
    // 的 XAML 触发器同名（改键名要同步：这里 + 两字典 + MainWindow.xaml 触发器）。
    // Kind=新模板/统计条的六态业务口径（Alarm=红灯占用、Gate=待门控、Curing=计时）。

    /// <summary>空闲/待门控/计时中/放行/红灯占用/未配置（统计条与卡片触发器同源）。</summary>
    private string _kind = "Idle";
    public string Kind
    {
        get => _kind;
        private set => Set(ref _kind, value);
    }

    private int _planMinutes;
    /// <summary>最近一拍"空闲计划时长"分钟（策略三级解析值；非空闲拍不更新）。
    /// 缓存它而不是在属性里存文本——单位切换时 RefreshUnitText 重拼"计划 X"。</summary>
    private string _planText = "";
    /// <summary>
    /// 空闲卡计划时长预览（V0.0.58）："计划 2小时"/"计划 120 分"（随显示单位
    /// 走 DurationUnit）；策略未知时"计划 —"。只在 Kind=Idle 的拍有值，其它态
    /// 清空（XAML 以 Kind=Idle 触发器控显隐，文本空行不出现）。
    /// </summary>
    public string PlanText
    {
        get => _planText;
        private set => Set(ref _planText, value);
    }

    /// <summary>"计划 X"文本拼装（分钟口径"N分"；小时口径 HourText 智能省略零段）。</summary>
    private static string FormatPlan(int planMinutes) => planMinutes > 0
        ? $"计划 {DurationUnit.Text(planMinutes)}"
        : "计划 —";

    private int _remainMin;
    /// <summary>剩余固化分钟（状态机 Curing 精确值；灯色反推=0）。</summary>
    public int RemainMin
    {
        get => _remainMin;
        private set => Set(ref _remainMin, value);
    }

    // ---------- 时长显示文本（随显示单位：V0.0.56 策略窗切时/分主窗同步） ----------
    //
    // 分钟口径保持旧显示："47"(26号) + "分"(11号)、副文"共 120 分"；小时口径
    // 数字 26 号、单位字 11 号（与"分"后缀同字号，V0.0.56 二轮用户拍板）：
    // ("1"(26) "小时"(11) "28"(26) "分"(11))——四段交替、第二组仅 >1 小时有值。
    // 数值源仍是分钟（RemainMin/TotalMin），文本在 ApplyKind 一拍内同步落。

    private string _remainNum1 = "0";
    /// <summary>大数字第一段数字（26 号；分钟/不足 1 小时=全部数字）。</summary>
    public string RemainNum1
    {
        get => _remainNum1;
        private set => Set(ref _remainNum1, value);
    }

    private string _remainUnit1 = "小时";
    /// <summary>第一段单位字（11 号；"分"/"小时"——缺省=小时口径，V0.0.84）。</summary>
    public string RemainUnit1
    {
        get => _remainUnit1;
        private set => Set(ref _remainUnit1, value);
    }

    private string _remainNum2 = "";
    /// <summary>大数字第二段数字（"分"部分；仅 >1 小时有值，否则空串）。</summary>
    public string RemainNum2
    {
        get => _remainNum2;
        private set => Set(ref _remainNum2, value);
    }

    private string _remainUnit2 = "";
    /// <summary>第二段单位字（"分"；仅 >1 小时有值，否则空串）。</summary>
    public string RemainUnit2
    {
        get => _remainUnit2;
        private set => Set(ref _remainUnit2, value);
    }

    private bool _remainHasTail;
    /// <summary>有大数字第二段（=Num2 非空，"1小时28分"形态）——卡内第二组显隐开关。</summary>
    public bool RemainHasTail
    {
        get => _remainHasTail;
        private set => Set(ref _remainHasTail, value);
    }

    private string _totalDisplay = "0分";
    /// <summary>卡内副文中段："共 {文本} · 已固化 N%"（分钟="120 分"；小时="2小时"）。
    /// TotalMin=0 的卡片走状态字视图不显示，数值仍落缺省（不变量兜底）。</summary>
    public string TotalDisplay
    {
        get => _totalDisplay;
        private set => Set(ref _totalDisplay, value);
    }

    private int _totalMin;
    /// <summary>本仓锁定总时长分钟（>0 才有精确计时数据）。</summary>
    public int TotalMin
    {
        get => _totalMin;
        private set
        {
            if (Set(ref _totalMin, value))
                Raise(nameof(HasDetail)); // 派生属性手动补通知（卡内"大数字/状态字"切换依据）
        }
    }

    private int _progressPct;
    /// <summary>进度百分比 0~100（(总-剩)/总；未计时=0）——卡内副文"已固化 N%"用。</summary>
    public int ProgressPct
    {
        get => _progressPct;
        private set => Set(ref _progressPct, value);
    }

    private double _progressRatio;
    /// <summary>
    /// 进度比 0.0~1.0（卡内进度条 ScaleTransform.ScaleX 直绑：Rectangle 整宽
    /// 压缩到该比例即填充段；比换算 ActualWidth×比例的转换器方案轻）。
    /// 与 ProgressPct 同源同拍（ApplyKind 一处算），不变量同：TotalMin=0 → 0。
    /// </summary>
    public double ProgressRatio
    {
        get => _progressRatio;
        private set => Set(ref _progressRatio, value);
    }

    /// <summary>有精确计时数据（状态机 Curing 且 TotalMin&gt;0）——卡内大数字+进度条只在它成立时展示，
    /// 否则显示状态字（灯色反推的"计时中"没有精确数值，硬显 0 反误导）。</summary>
    public bool HasDetail => TotalMin > 0;

    /// <summary>层名（"上层/中层/下层"）——卡片尾部一行与 ToolTip 共用；
    /// Rack/Level 构造后不变，计算属性无需 INPC。</summary>
    public string LayerName => LevelName(Level);

    /// <summary>统一落 Kind/Remain/Total/Progress（保持不变量：Total=0 → Remain=0、Pct=0、Ratio=0）。</summary>
    private void ApplyKind(string kind, int remainMinutes, int totalMinutes)
    {
        Kind = kind;
        TotalMin = Math.Max(0, totalMinutes);
        RemainMin = TotalMin > 0 ? Math.Clamp(remainMinutes, 0, TotalMin) : 0;
        ProgressRatio = TotalMin > 0
            ? Math.Clamp((TotalMin - RemainMin) / (double)TotalMin, 0, 1)
            : 0;
        ProgressPct = TotalMin > 0
            ? Math.Clamp((int)Math.Round(100.0 * (TotalMin - RemainMin) / TotalMin,
                MidpointRounding.AwayFromZero), 0, 100)
            : 0;
        SyncDisplayTexts();
        // 空闲计划预览（V0.0.58）：只对 Idle 态给"计划 X"字面（策略三级解析值），
        // 其它态清空——XAML 以 Kind=Idle 触发器控显隐，非空闲卡不出现该行。
        PlanText = kind == "Idle" ? FormatPlan(_planMinutes) : "";
    }

    /// <summary>
    /// 一拍快照刷新（调用方 500ms 定时；数据从语义键直查快照）。
    /// machineStatus=状态机状态（非空闲时展示口径以状态机为准——剩余分钟数
    /// 灯色反推不出来）。状态词"计时中 剩X"在此按当前显示单位拼装
    /// （旧版取 Core SlotStatus.StateText——已随单位综合上移 UI 删除）。
    /// V0.0.67：快照带出 AlarmGrill 报警标志——计时中报警照常计时 Phase 仍
    /// Curing，报警标志优先于 Curing 分支把卡切 Alarm 口径（此前卡牌报警
    /// 永远显示不出，红灯 DO 反推分支在精确态路线下轮不到）。
    /// P3.3：productCode=库位绑定的产品条码（空=未绑定）；bindingMissing=
    /// 托盘在架却没绑定（库位码下方显示红字"未绑定"，只提示不拦截固化流程）。
    /// </summary>
    /// <summary>产品码行文案（V0.0.84 摘要口径；V0.0.85 阈值随行数档联动）：未绑定返回
    /// "未绑定"（红字触发器吃 BindingMissing）；码短原样；超容量才摘要保头尾。
    /// 为什么掐中间：现场扫码对货以**尾号**对账、头部是型号/批次前缀，中间流水段
    /// 剩余价值最低。动态行数区（V0.0.83 两行 32 → V0.0.84 三行 48/四行 64，用户
    /// 拍板"两行显示不完就三行，三行不行就四行"，见 MainWindow.xaml「产品码 SN」
    /// 注释与 SnZoneOf）——13 字符/行按最窄正常窗口保守定标（≈1280 宽下 10 列卡
    /// 文本 ≈104px，11 号 ≈7.5px/字符）。**容量=摘要阈值=maxRows×13**：档 4 → 52
    /// （V0.0.84 同值）、3 → 39、2 → 26、1 → 13；容量内原样换行完整可见。
    /// 被截（>容量）才摘要"头 6+…+尾 10"（17 字符 ≈1.3 行，两行区必装下）；
    /// 1 档是极矮卡底线（区只够一行），摘要缩为"头 4+…+尾 6"（11 字符单行）。
    /// 想调阈值口径：改这里的 ×13 与 CuringDisplayTests 的 SN 边界用例。</summary>
    private static string SnDisplayText(string productCode, bool bindingMissing, int maxRows = 4)
    {
        if (bindingMissing)
        {
            return "未绑定";
        }

        var code = productCode ?? string.Empty;
        // 容量阈值 = SN 最大行数档 × 单行保守容量 13（V0.0.85 随小屏降档联动）
        var threshold = maxRows * 13;
        if (code.Length <= threshold)
        {
            return code;
        }

        // 保头尾摘中段：档 2~4 "头 6+…+尾 10"；档 1 缩 "头 4+…+尾 6"（单行装下）
        const int keepHeadWide = 6;        // 头部保留：型号/批次前缀
        const int keepTailWide = 10;       // 尾部保留：对货核号的流水段
        return maxRows <= 1
            ? code.Substring(0, 4) + "…" + code.Substring(code.Length - 6, 6)
            : code.Substring(0, keepHeadWide) + "…" + code.Substring(code.Length - keepTailWide, keepTailWide);
    }

    /// <summary>
    /// SN 换行区分档（V0.0.84 建，V0.0.85 加行数档上限，Height 与 mid 补偿的
    /// 唯一计算口）：行数按**显示文本**（摘要后的 BindingText）长度保守估算
    /// ——最窄正常窗口单行 ≈13 字符（104px / 7.5px，V0.0.83 口径），
    /// ceil(len/13) 封顶 **maxRows**（缺省 4）；1~2 行恒 32 高（V0.0.83 定标：
    /// 短码/摘要码锚点零移动），3 行 48、4 行 64（用户拍板"两行显示不完就
    /// 三行，三行不行就四行"）。小屏降档（V0.0.85）：maxRows 由主窗实测卡高
    /// 经 SnMaxRowsForCardHeight 下发——1 档是极矮卡底线，区高塌到单行 16
    /// （此时"两行 32 锚点"让位给"不与固化时间重叠"的更高优先级，同窗 30 卡
    /// 全体同塌、观感一致）。估算只可能 ≥ 实际需要行数（窗口更宽时每行装更多、
    /// 实际换行更少），偏高方向的底部留白由垂直居中消化，永不裁字。
    /// 补偿 = Margin-top 4 + 区高（32→36、48→52、64→68、16→20），
    /// mid 大数字锚点恒与无 SN 卡一致。
    /// </summary>
    private static (double Height, System.Windows.Thickness Compensation) SnZoneOf(string shown, int maxRows = 4)
    {
        const double charsPerLine = 13;   // 单行保守容量（最窄正常窗口口径，V0.0.83 定标）
        if (string.IsNullOrEmpty(shown))
        {
            return (32, new(0));          // 整区 Collapsed：区高回缺省、补偿清零
        }

        var lines = Math.Min(maxRows, (int)Math.Ceiling(shown.Length / charsPerLine));
        var height = maxRows <= 1
            ? SnLineHeight                                   // 1 档：单行区 16（小屏底线）
            : Math.Max(32, lines * SnLineHeight);           // 2 档起保 32：锚点零移动
        return (height, new(0, 0, 0, SnZoneTopMargin + height));
    }

    /// <summary>
    /// SN 展示四属性同拍重算（文案 → 占位 → 区高 → mid 补偿，防彼生异步抖动）：
    /// Refresh 每拍与 SnMaxRows 降档共用——档变化时用已存 ProductCode/BindingMissing
    /// 立即重算（RefreshSnPresentation 不等下一拍，窗口拖动中降档/升档立刻反映）。
    /// </summary>
    private void RecomputeSnPresentation()
    {
        BindingText = SnDisplayText(ProductCode, BindingMissing, _snMaxRows);
        HasBindingText = !string.IsNullOrEmpty(BindingText);
        var zone = SnZoneOf(BindingText, _snMaxRows);
        SnZoneHeight = zone.Height;
        SnZoneCompensation = zone.Compensation;
    }

    public void Refresh(PointTableSnapshot snapshot, bool hasTable, SlotStatus? machineStatus = null,
        int planMinutes = 0, string productCode = "", bool bindingMissing = false)
    {
        // 绑定信息与流程状态无关，每拍先落（即使点表未配置也照常显示已存绑定）。
        ProductCode = productCode;
        BindingMissing = bindingMissing && string.IsNullOrWhiteSpace(productCode);
        RecomputeSnPresentation();   // V0.0.85：文案/区高/补偿同拍收口（行数档随卡高联动）

        _planMinutes = planMinutes;   // 缓存本拍策略解析值（ApplyKind 落 Idle 字面 / 单位切换重拼用）
        _lastCuring = machineStatus is { Phase: SlotPhase.Curing } c ? c : null;
        if (!hasTable)
        {
            SlotStateText = "未配置";
            ApplyKind("NotConfigured", 0, 0);
            return;
        }

        var tray = snapshot.Get(new(true, IoPointRole.TrayDetect, Rack, Level));
        var grillL = snapshot.Get(new(true, IoPointRole.RackLightL, Rack, Level));
        var grillR = snapshot.Get(new(true, IoPointRole.RackLightR, Rack, Level));
        var red = snapshot.Get(new(false, IoPointRole.LampRed, Rack, Level));
        var yellow = snapshot.Get(new(false, IoPointRole.LampYellow, Rack, Level));
        var green = snapshot.Get(new(false, IoPointRole.LampGreen, Rack, Level));

        TrayFoot = tray == true ? "◈ 托盘在" : tray == false ? "无托盘" : "—";
        GrillText = grillL == null && grillR == null ? "—"
            : $"L:{BoolText(grillL)} R:{BoolText(grillR)}";
        LampText = red == true ? "红灯" : yellow == true ? "黄灯"
            : green == true ? "绿灯" : "灭";
        SlotStateText = machineStatus switch
        {
            { AlarmGrill: true } => "占用",                          // V0.0.67：卡牌报警显出（与胶囊"占用"同词）
            { Phase: SlotPhase.Curing } st => DescribeCuring(st),
            { Phase: SlotPhase.Done } => "放行",
            { Phase: SlotPhase.WaitingGate } => "待门控",
            _ => mapState(tray, red, yellow, green),                 // 空闲走灯色反推
        };
        // Kind（新口径）：状态机给精确状态的仓位以状态机为准（含剩余分钟）；
        // 计时中光栅报警照常计时但 Phase 仍 Curing（客户口径"计时不变"），
        // 报警标志优先于 Curing 显示——卡切 Alarm 口径（红卡+脉冲+胶囊"占用"），
        // 剩余分钟照传（数值不变量保持完整，复位后自愈回 Curing 视图）。
        // 状态机空闲/未涉足的仓位灯色反推——红=Alarm（占用）、黄=Curing（无精确
        // 数值，HasDetail=false 显示状态字）、绿=Done、托盘在=Gate（待判定）、全空=Idle。
        if (machineStatus is { AlarmGrill: true } alert)
            ApplyKind("Alarm", alert.RemainMinutes, alert.TotalMinutes);
        else if (machineStatus is { Phase: SlotPhase.Curing } cur)
            ApplyKind("Curing", cur.RemainMinutes, cur.TotalMinutes);
        else if (machineStatus is { Phase: SlotPhase.Done })
            ApplyKind("Done", 0, 0);
        else if (machineStatus is { Phase: SlotPhase.WaitingGate })
            ApplyKind("Gate", 0, 0);
        else if (red == true) ApplyKind("Alarm", 0, 0);
        else if (yellow == true) ApplyKind("Curing", 0, 0);
        else if (green == true) ApplyKind("Done", 0, 0);
        else if (tray == true) ApplyKind("Gate", 0, 0);
        else ApplyKind("Idle", 0, 0);
    }

    private static string BoolText(bool? v) => v == true ? "ON" : v == false ? "OFF" : "—";

    private static string mapState(bool? tray, bool? red, bool? yellow, bool? green) =>
        red == true ? "占用" : yellow == true ? "计时中" : green == true ? "放行"
            : tray == true ? "待判定" : "空闲";

    /// <summary>最近一拍的 Curing 状态（单位切换时重拼"剩X"用；非 Curing=null）。</summary>
    private SlotStatus? _lastCuring;

    /// <summary>
    /// Curing 状态词（旧版 Core StateText 版式等价）："计时中 剩{文本}"（剩余>0，
    /// 文本随显示单位：分钟"45分"/小时"1小时28分"），纯"计时中"（无精确剩余）。
    /// </summary>
    private string DescribeCuring(SlotStatus status) =>
        status.RemainMinutes > 0 ? $"计时中 剩{DurationUnit.Text(status.RemainMinutes)}" : "计时中";

    /// <summary>
    /// 显示单位切换（策略窗 Change 广播后的重写路径）：只重写字面——数值
    /// （Remain/Total/进度）不动；"剩X"状态词用 _lastCuring 缓存重拼，其余
    /// 场景 500ms 下一拍自愈。须在 UI 线程调（与 Refresh 同口径）。
    /// </summary>
    public void RefreshUnitText()
    {
        if (_lastCuring is { AlarmGrill: true })
            SlotStateText = "占用";   // 报警快照（Phase=Curing）重拼不回退"计时中"字面——下一次 500ms 拍才自愈
        else if (_lastCuring is { } s && s.RemainMinutes > 0)
            SlotStateText = DescribeCuring(s);
        // 空闲计划预览随显示单位换口径（"计划 2小时"↔"计划 120 分"——用
        // 最近一拍缓存的 _planMinutes 重拼，不等 500ms 拍；非 Idle 态清空兜底）。
        PlanText = Kind == "Idle" ? FormatPlan(_planMinutes) : "";
        SyncDisplayTexts();
    }

    /// <summary>按当前显示单位落大数字四段/显隐开关与副文（数值源恒分钟；ApplyKind 与单位切换共用）。</summary>
    private void SyncDisplayTexts()
    {
        bool isHours = DurationUnit.Current == PolicyTimeUnit.Hours;
        var (num1, unit1, num2, unit2) = DurationUnit.SplitBig(RemainMin);
        RemainNum1 = num1;
        RemainUnit1 = unit1;
        RemainNum2 = num2;
        RemainUnit2 = unit2;
        RemainHasTail = !string.IsNullOrEmpty(num2);
        TotalDisplay = isHours ? DurationUnit.Text(TotalMin) : $"{TotalMin} 分";
    }
}

/// <summary>
/// 固化架分组（V0.0.20 双架支持）：一台固化架 = 一个分组 = 一块 10×3 网格，
/// 主窗按组上下堆叠（组间带标题条），贴机台实景（两台架各 10 站 3 层）。
/// 组内卡片是 <see cref="MainViewModel.Slots"/> 平铺集合的同批实例（引用共享，
/// 500ms 刷一遍即两组同步），构建后全部只读、无需 INPC。
/// </summary>
public sealed class RackGroupViewModel
{
    /// <summary>分组标题（"1号固化架"/"2号固化架"——全局架号段归属）。</summary>
    public string Title { get; }

    /// <summary>组内仓位卡（行优先：先层后架，与平铺契约一致）。</summary>
    public IReadOnlyList<RackSlotViewModel> Slots { get; }

    /// <summary>组内列数（每架 10 站，与 SlotLocationCode.RacksPerRow 同源）。</summary>
    public int Columns => SlotLocationCode.RacksPerRow;

    /// <summary>组内行数（层数，跟随点表）。</summary>
    public int Rows { get; }

    /// <summary>是否显示分组标题条：单架（组数=1）不显示，主窗视觉与单架版完全一致。</summary>
    public bool IsHeaderVisible { get; }

    public RackGroupViewModel(string title, IReadOnlyList<RackSlotViewModel> slots,
        int rows, bool isHeaderVisible)
    {
        Title = title;
        Slots = slots;
        Rows = rows;
        IsHeaderVisible = isHeaderVisible;
    }
}

/// <summary>主窗 VM。总线生命周期：Dispose 与重建；競争以 lock 保证。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _refreshTimer;
    private readonly object _busLock = new();

    /// <summary>
    /// 光栅报警"人工复位"弹窗编排器（V0.0.69，详情见 GrillAlarmResetCoordinator）。
    /// MainViewModel 持有唯一实例：500ms 拍/复位回刷喂报警清单 → 差分热更 + 弹窗
    /// 开关决策 → 复位请求经事件派回本类（Task.Run 后台调状态机，铁律 2）。
    /// public 供测试直驱（xUnit 无 InternalsVisibleTo，主窗 500ms 拍私有不便测）。
    /// </summary>
    public GrillAlarmResetCoordinator AlarmPresenter { get; } = new(
        () => GrillAlarmResetModeHub.Current);

    public ObservableCollection<RackSlotViewModel> Slots { get; } = new();

    /// <summary>最近一次下发的 SN 行数档（缺省 4；View 未挂接实测时卡片自持缺省，不漂移）。</summary>
    private int _snMaxRowsDispatched = 4;

    /// <summary>
    /// 主窗实测卡片渲染高度喂进来（MainWindow code-behind 量第一张卡——30 卡
    /// UniformGrid 均分同高，量一张代表全部；窗口 resize 中持续触发）。
    /// 换算全局 SN 行数档并**档位变化才**下发 30 卡（值不同才走，防拖动中
    /// SizeChanged 高频重复下发）；下发正文 RackSlotViewModel.SnMaxRows 的
    /// setter 自带"重算 SN 文案/区高/补偿"收口，本轮无需再调 Refresh。
    /// 找不到卡片实测点（极端异常）时此方法不被调，档恒 4、行为与 V0.0.84 一致——
    /// 自适应只在确有实测时生效（fail-safe 到旧行为）。
    /// UI 线程调用（SizeChanged 事件线程；测试直调也安全——Set 封送兜底）。
    /// </summary>
    public void UpdateSnCardHeight(double cardHeight)
    {
        var rows = RackSlotViewModel.SnMaxRowsForCardHeight(cardHeight);
        if (rows == _snMaxRowsDispatched)
            return;
        _snMaxRowsDispatched = rows;
        foreach (var slot in Slots)   // 组内卡与平铺 Slots 同批实例，平铺集遍历即全覆盖
            slot.SnMaxRows = rows;
    }

    /// <summary>
    /// 固化架分组视图（V0.0.20 双架支持）：主窗主内容按组上下堆叠——
    /// 单架 1 组（无标题条，视觉同单架版），双架 2 组（各带"N号固化架"标题条）。
    /// 组内卡片与 Slots 同批实例。
    /// </summary>
    public ObservableCollection<RackGroupViewModel> RackGroups { get; } = new();

    /// <summary>分组数——主窗外层 UniformGrid.Rows 绑定它（纵向每组均分一档）。</summary>
    private int _groupCount = 1;
    public int GroupCount
    {
        get => _groupCount;
        private set => Set(ref _groupCount, value);
    }

    /// <summary>
    /// 网格列数（每排站数/架数）——主窗 UniformGrid.Columns 绑定它：
    /// 窗口拉伸（全屏/不同分辨率）时 10 张卡均分一行宽度，与机台"一排 10 站"一致。
    /// </summary>
    private int _rackCount = 10;
    public int RackCount
    {
        get => _rackCount;
        private set => Set(ref _rackCount, value);
    }

    /// <summary>网格行数（层数）——UniformGrid.Rows 绑定，三排=上中下三层。</summary>
    private int _levelCount = 3;
    public int LevelCount
    {
        get => _levelCount;
        private set => Set(ref _levelCount, value);
    }

    // 顶栏连接文本两段口径（V0.0.37 改造，用户目检"顶栏太挤还显示不完整"）：
    //   ConnectionText = 短词（抓重点：已连接/未连接/台数进度/演示态）——直接显示；
    //   ConnectionDetailText = 详情全文（总线摘要或错误串如"XX:连接超时（3000ms），
    //   请检查 IP/端口"）——顶栏悬停 ToolTip 展示，长文不再怼上顶栏栏面。
    private string _connectionText = "未启动";
    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    private string _connectionDetailText = "未启动";
    public string ConnectionDetailText
    {
        get => _connectionDetailText;
        private set => Set(ref _connectionDetailText, value);
    }

    /// <summary>
    /// 顶栏连接短词派生（纯函数，可测试）。规则：
    /// ① 起停/配置态自成短词原样显示；② 演示模式语义独立（文本已短）；
    /// ③ 连通→"已连接"；④ 断开但仍有部分台在线（"1/2 台已连"收尾）保留台数进度，
    ///    全断（"0/N 台已连"）或任意错误串出"未连接"——与灯点红绿同拍呼应。
    /// </summary>
    public static string DeriveConnectionShort(bool connected, string summary)
    {
        if (string.IsNullOrEmpty(summary) || summary is "未启动" or "已停止" or "未配置设备")
            return summary;
        if (summary.StartsWith("演示模式"))
            return summary;
        if (connected)
            return "已连接";
        // 台数进度概要形如 "N/M 台已连"（以"台已连"收尾才认——错误详情里也可能
        // 出现 '/'，如"请检查 IP/端口"，不能借 '/')
        if (summary.EndsWith("台已连"))
        {
            int slash = summary.IndexOf('/');
            if (slash > 0 && summary[..slash] != "0")
                return summary;      // 部分台在线（如 1/2 台已连）——进度口径更有信息量
        }
        return "未连接";
    }

    /// <summary>顶栏连接两段文本一次同步（灯点 + 短词 + 详情全文，三处同拍）。</summary>
    private void SetConnectionTexts(bool connected, string summary)
    {
        IsConnected = connected;
        ConnectionText = DeriveConnectionShort(connected, summary);
        ConnectionDetailText = summary;
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    /// <summary>当前生效策略（顶栏展示；策略窗激活后经 LoadPolicyBook 刷新）。</summary>
    private string _policyText = "策略：未加载";
    public string PolicyText
    {
        get => _policyText;
        private set => Set(ref _policyText, value);
    }

    /// <summary>当前登录操作员（顶栏展示；登录窗带出，角色是各窗门禁依据）。</summary>
    private string _currentUserName = "";
    private OperatorRole? _currentRole;

    private string _operatorText = "未登录";
    public string OperatorText
    {
        get => _operatorText;
        private set => Set(ref _operatorText, value);
    }

    /// <summary>是否有操作员在线（未登录时各功能入口一律拦下）。</summary>
    public bool IsLoggedIn => _currentRole.HasValue;

    /// <summary>空闲超时监视器（View 层打点入口：MainWindow ctor 挂进程级
    /// InputManager，任意鼠标键盘都调 NotifyActivity；判定与登出见 _idleMonitor/
    /// OnIdleLogout）。</summary>
    public IdleLogoutMonitor IdleMonitor => _idleMonitor;

    /// <summary>
    /// "设置"按钮可用性（V0.0.49 用户拍板；V0.0.95 扩展未登录）：operator
    /// 登录后整钮禁用、未登录同样禁用——下拉里全是管理功能，无权限的角色
    /// 展开逐项被 Gate 拦不如直接置灰；悬停反馈走 SettingsToolTip（禁用态
    /// 也显示，XAML ToolTipService.ShowOnDisabled=True；WPF 禁用钮不响应
    /// 点击，无弹窗路径）。
    /// </summary>
    public bool IsSettingsEnabled => _currentRole != null && _currentRole != OperatorRole.Operator;

    /// <summary>
    /// 设置钮悬停提示（XAML 已开 ToolTipService.ShowOnDisabled，禁用态也显示）：
    /// 未登录/operator 各给一句去处说明；admin+/dev 可用态给下拉功能清单。
    /// 登录/登出处与 IsSettingsEnabled 同拍失效刷新（两处 Raise 一并改）。
    /// </summary>
    public string SettingsToolTip => _currentRole switch
    {
        null => "未登录——设置收拢的是管理功能，请先登录",
        OperatorRole.Operator => "操作员无管理权限（除工单外），设置不可用",
        _ => "重新连接 / 演示 / 用户管理 / 工艺策略 / 过账配置 / IO 测试 / 点表 / 软件激活 / 主题",
    };

    /// <summary>
    /// "点表配置"菜单项可见性：admin 隐藏此项（用户拍板"直接没有这个选项"）；
    /// 未登录/操作员仍显示（未登录设置整钮禁用打不开下拉；operator 同）。
    /// 只有 dev（真开发者账号）能见到点表配置。
    /// </summary>
    public bool ShowPointTableItem => _currentRole != OperatorRole.Admin;

    /// <summary>
    /// "扫码枪配置"菜单项可见性（V0.0.77）：**仅 dev 登录后显示**——
    /// 扫码枪属设备接入参数，与点表同级；未登录/admin/operator 一律直接没有
    /// 这一项（不是置灰）。命令入口另有 Gate(Dev) 兜底。
    /// </summary>
    public bool ShowScannerConfigItem => _currentRole == OperatorRole.Dev;

    /// <summary>单线过账状态（顶栏展示；过账服务后台线程回调，经 UiInvoke 封送）。</summary>
    private string _postingText = "过账：—";
    public string PostingText
    {
        get => _postingText;
        private set => Set(ref _postingText, value);
    }

    /// <summary>
    /// 顶栏软件授权短文案（"授权：永久 / 试用剩 N 天 / 未激活 / 已过期 / 指纹不符"）。
    /// 启动门禁已 Refresh；运行期从"设置→软件激活"成功后刷新。
    /// </summary>
    private string _licenseText = "授权：—";
    public string LicenseText
    {
        get => _licenseText;
        private set => Set(ref _licenseText, value);
    }

    /// <summary>过账是否启用（工单卡角标 DataTrigger 出"过账启用/过账未启用"文案）。</summary>
    private bool _postingEnabled;
    public bool PostingEnabled
    {
        get => _postingEnabled;
        private set => Set(ref _postingEnabled, value);
    }

    /// <summary>演示模式当前态（true=Mock 总线流转中；顶栏开关按钮 DataTrigger 高亮）。</summary>
    private bool _isSimulate;
    public bool IsSimulate
    {
        get => _isSimulate;
        private set => Set(ref _isSimulate, value);
    }

    /// <summary>演示开关按钮文案：显示当前模式（"演示：开"=Mock 演示中 / "演示：关"=真机通讯中）。</summary>
    private string _simulateText = "演示：关";
    public string SimulateText
    {
        get => _simulateText;
        private set => Set(ref _simulateText, value);
    }

    // ---------- 扫码枪 + 库位绑定（P3.3 扫码工序） ----------
    // 现场流程：托盘入库前先扫库位码（A01~J12）再扫产品二维码 → 绑定；
    // 固化完成取件后绑定作废（状态机 TrayLeft 清），下次入库必须重新扫。
    // 未绑定的在架托盘只做界面报警提示，不拦截固化流程（客户口径）。

    private ScannerOptions _scannerOptions = new();
    private ScannerBarcodeService? _scanner;
    private SlotBindingSession _bindings = new();

    /// <summary>
    /// 扫码枪"关旧→开新"的生命周期锁（配置窗保存热重启用）：串口是独占资源、
    /// TCP 也可能与旧会话同目标竞争，旧服务 Dispose 未完成就开新口会打开失败/
    /// 挤占；Start/Dispose 都在后台任务里串行执行（最坏等收码线程 3 秒，UI
    /// 不阻塞——铁律 2）。
    /// </summary>
    private readonly object _scannerLifecycleGate = new();

    /// <summary>已提示过"未绑定"的库位（日志只记边沿：新出现的未绑定才写一行）。</summary>
    private readonly HashSet<(int Rack, int Level)> _unboundAlerted = new();

    /// <summary>最近一拍"托盘在架但未绑定产品"的库位数（扫码胶囊报警依据）。</summary>
    private int _unboundTrayCount;

    /// <summary>3 秒错误浮层文案（扫错库位码时显示；优先级高于常规文案）。</summary>
    private string _scanHintText = "";

    /// <summary>错误浮层到期时刻（过点后 RefreshScanState 自动回常规文案）。</summary>
    private DateTime _scanHintUntil;

    private string _scanText = "扫码枪未启用";
    /// <summary>统计条扫码胶囊文案（阶段/未绑定报警/连接态三档，RefreshScanState 唯一写点）。</summary>
    public string ScanText
    {
        get => _scanText;
        private set => Set(ref _scanText, value);
    }

    private string _scanTip = "";
    /// <summary>扫码胶囊悬停说明（怎么配置/当前在等什么码）。</summary>
    public string ScanTip
    {
        get => _scanTip;
        private set => Set(ref _scanTip, value);
    }

    private bool _scanWaitingProduct;
    /// <summary>正在等产品码（黄点 + 显示【取消】按钮）。</summary>
    public bool ScanWaitingProduct
    {
        get => _scanWaitingProduct;
        private set => Set(ref _scanWaitingProduct, value);
    }

    private bool _scanWarning;
    /// <summary>有在架托盘未绑产品（红点报警；只提示不拦截流程）。</summary>
    public bool ScanWarning
    {
        get => _scanWarning;
        private set => Set(ref _scanWarning, value);
    }

    private bool _scanConnected;
    /// <summary>扫码枪已连接（绿点；未连接时灰点，子模块后台自动重连）。</summary>
    public bool ScanConnected
    {
        get => _scanConnected;
        private set => Set(ref _scanConnected, value);
    }

    // ---------- 顶部统计条（方案 D，2026-10-29；RefreshSlots 每拍聚合） ----------
    // 五态口径与卡片 Kind 同名（Idle/Gate/Curing/Done/Alarm），详见 SlotStatistics。

    private int _idleCount;
    public int IdleCount { get => _idleCount; private set => Set(ref _idleCount, value); }

    private int _curingCount;
    public int CuringCount { get => _curingCount; private set => Set(ref _curingCount, value); }

    private int _gateCount;
    public int GateCount { get => _gateCount; private set => Set(ref _gateCount, value); }

    private int _doneCount;
    public int DoneCount { get => _doneCount; private set => Set(ref _doneCount, value); }

    private int _alarmCount;
    public int AlarmCount { get => _alarmCount; private set => Set(ref _alarmCount, value); }

    private string _nextCloseText = "暂无计时";
    /// <summary>⚡剩余最短（计时中最早到点的库位，操作员一眼锁定下一个取件口）。</summary>
    public string NextCloseText
    {
        get => _nextCloseText;
        private set => Set(ref _nextCloseText, value);
    }

    // ---------- 下部工单带（信息带） ----------
    // 数据源=config/workorder.json（WorkOrderStore）。LastWriteTime 变了才重读
    // （500ms 同拍不反复碰磁盘）；工单窗保存返回 true 也主动刷一次。

    private string _workOrderRcNoR = "—";
    public string WorkOrderRcNoR { get => _workOrderRcNoR; private set => Set(ref _workOrderRcNoR, value); }

    private string _workOrderQtyR = "0/0";
    public string WorkOrderQtyR { get => _workOrderQtyR; private set => Set(ref _workOrderQtyR, value); }

    private int _workOrderPctR;
    public int WorkOrderPctR { get => _workOrderPctR; private set => Set(ref _workOrderPctR, value); }

    private string _workOrderRcNoL = "—";
    public string WorkOrderRcNoL { get => _workOrderRcNoL; private set => Set(ref _workOrderRcNoL, value); }

    private string _workOrderQtyL = "0/0";
    public string WorkOrderQtyL { get => _workOrderQtyL; private set => Set(ref _workOrderQtyL, value); }

    private int _workOrderPctL;
    public int WorkOrderPctL { get => _workOrderPctL; private set => Set(ref _workOrderPctL, value); }

    // ---------- 工单带显隐（单/双工位，V0.0.47） ----------
    // [CuringMes] DualSide 开关的 UI 面子：双工位 = R/L 两张卡并排（V0.0.20 老口径）；
    // 单工位 = 只显 Side 侧一张卡且文案去 R/L 工位字样（V0.0.38 工单窗同口径——
    // 视图↔槽映射恒定 R↔Right/L↔Left，隐藏侧数据照常刷新不特殊处理，切回双工位立见）。

    private string _mainSide = "R";
    /// <summary>单工位所用工位（规范化后的 "R"/"L"）——只显哪张卡的依据；双工位不参与显隐。</summary>
    public string MainSide
    {
        get => _mainSide;
        private set
        {
            if (Set(ref _mainSide, value))
            {
                Raise(nameof(ShowWoCardR));
                Raise(nameof(ShowWoCardL));
            }
        }
    }

    private bool _isDualSide;
    /// <summary>双工位模式（[CuringMes] DualSide=1）——工单带两张卡全显；false 只显一张。</summary>
    public bool IsDualSide
    {
        get => _isDualSide;
        private set
        {
            if (Set(ref _isDualSide, value))
            {
                Raise(nameof(ShowWoCardR));
                Raise(nameof(ShowWoCardL));
                Raise(nameof(WoCardColumns));
            }
        }
    }

    /// <summary>R 工位卡是否显示（单工位时 Side==R 才显；双工位恒显）。</summary>
    public bool ShowWoCardR => IsDualSide || MainSide == "R";

    /// <summary>L 工位卡是否显示（单工位时 Side==L 才显；双工位恒显）。</summary>
    public bool ShowWoCardL => IsDualSide || MainSide == "L";

    /// <summary>工单带列数：双工位 2 张并排 / 单工位 1 张独占整宽（UniformGrid 跳过 Collapsed 卡不占格）。</summary>
    public int WoCardColumns => IsDualSide ? 2 : 1;

    // ---------- 主题切换（双主题 G2/G1） ----------

    private string _toggleThemeText = "深色";
    /// <summary>主题切换按钮文案：显示【目标】主题名——浅色下显示"深色"（点了切去 G1），反之"浅色"。</summary>
    public string ToggleThemeText
    {
        get => _toggleThemeText;
        private set => Set(ref _toggleThemeText, value);
    }

    private PointTableConfig? _config;
    private IPointTableBus? _bus;
    private SlotCuringMachine? _machine;   // 仓位状态机（与 VM 同生命周期，总线可换不丢流程）
    private CuringPolicyBook? _policyBook; // 工艺策略库（策略窗保存后经 LoadPolicyBook 重读）
    private CuringMesPoster? _poster;      // 单线过账服务（P3.2 后半；配置热更新不换实例）
    private readonly IdleLogoutMonitor _idleMonitor; // 登录空闲超时监视（打点在 View 层 InputManager）
    private readonly int _idleLogoutMinutes;         // 空闲上限分钟（ctor 读 ini 缓存；<=0=不启用）

    public RelayCommand OpenPointTableCommand { get; }
    public RelayCommand OpenCuringPolicyCommand { get; }
    public RelayCommand OpenPostConfigCommand { get; }
    public RelayCommand OpenScannerConfigCommand { get; }
    public RelayCommand ReconnectCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand OpenWorkOrderCommand { get; }
    public RelayCommand OpenAccountsCommand { get; }
    public RelayCommand OpenIoTestCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ToggleSimulateCommand { get; }
    public RelayCommand OpenLicenseCommand { get; }
    public RelayCommand CancelScanCommand { get; }

    public MainViewModel(IDialogService dialogs, IdleLogoutMonitor? idleMonitor = null)
    {
        _dialogs = dialogs;

        // 登录空闲自动退出（V0.0.93）：上限分钟读 [setting] AutoLogoutMinutes
        // （缺省 15，0/负/非法=不启用；ctor 读一次缓存，改 ini 重启生效，同
        // ScanIntervalMs 口径——非热调参数，不做每拍 ini IO）。监视器可注入
        //（测试假时钟）；打点在 View 层 InputManager（MainWindow ctor），判定
        // 随 _refreshTimer 刷新拍，登出链见 OnIdleLogout。
        _idleLogoutMinutes = ParseIdleMinutes();
        _idleMonitor = idleMonitor ?? new IdleLogoutMonitor(() => _idleLogoutMinutes);
        _idleMonitor.LogoutDue += OnIdleLogout;

        OpenPointTableCommand = new RelayCommand(OpenPointTable);
        OpenCuringPolicyCommand = new RelayCommand(OpenCuringPolicy);
        OpenPostConfigCommand = new RelayCommand(OpenPostConfig);
        OpenScannerConfigCommand = new RelayCommand(OpenScannerConfig);
        ReconnectCommand = new RelayCommand(() => StartBus(_config));
        LoginCommand = new RelayCommand(Login);
        OpenWorkOrderCommand = new RelayCommand(OpenWorkOrder);
        OpenAccountsCommand = new RelayCommand(OpenAccounts);
        OpenIoTestCommand = new RelayCommand(OpenIoTest);
        ShowLogCommand = new RelayCommand(ShowLog);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleSimulateCommand = new RelayCommand(ToggleSimulate);
        OpenLicenseCommand = new RelayCommand(OpenLicense);
        // 扫码【取消】：结束当前"待扫产品码"回到等库位码（按钮只在待扫产品时显示）。
        CancelScanCommand = new RelayCommand(() =>
        {
            _bindings.Cancel();
            RefreshScanState();
        });
        // 顶栏授权文案：启动门禁已 Refresh 过（App.OnStartup），这里只读快照，
        // 不触发指纹采集/文件 IO。
        LicenseText = LicenseHost.ShortStatusText();
        // 授权状态热更：激活成功（TryActivate 收口 PublishStateEdge）/激活窗换码
        // 切换/每小时巡检产生边沿——顶栏文案随时跟随，换授权类型无需重启。
        //（LicenseHost 刷新点全在 UI 线程——边沿直接赋值，无需封送。）
        LicenseHost.StateChanged += OnLicenseStateChanged;
        // 主题按钮文案对齐 App.OnStartup 已应用的当前主题（测试态无 App：ThemeManager
        // 缺省 Light，同样落"深色"文案，现场首点即切 G1，行为一致）。
        ToggleThemeText = ThemeManager.Current == "Dark" ? "浅色" : "深色";
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        // 空闲超时判定随刷新拍（打点在 View 层 InputManager，见 _idleMonitor）。
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshSlots();
            _idleMonitor.Tick();
        };
        // 显示单位（时/分）联动（V0.0.56）：策略窗 DurationUnit.Change 切单位时
        // 广播——不等 500ms 拍，立即重刷卡片时长文本与统计条"⚡剩X"。事件在
        // UI 线程触发（策略窗 ComboBox 绑定链），订阅方同 RefreshSlots 线程口径。
        DurationUnit.Changed += OnDisplayUnitChanged;

        // 光栅报警人工复位接线（V0.0.69）：四事件链 + 模式切换广播。
        // 复位/全部复位走 Task.Run 后台（写灯=网络 IO，铁律 2），完成后回 UI
        // 立即刷清单（不等 500ms 拍，点掉哪行哪行即走）；弹窗开关直接转发
        // DialogService（非模态单例窗）。方法组订阅——Dispose 对称退订。
        AlarmPresenter.ShowRequested += OnAlarmShowRequested;
        AlarmPresenter.CloseRequested += OnAlarmCloseRequested;
        AlarmPresenter.ResetRequested += OnAlarmResetRequested;
        AlarmPresenter.ResetAllRequested += OnAlarmResetAllRequested;
        // 策略窗切"人工复位/自动复位"（GrillAlarmResetModeHub.Change）立即重新
        // 决策：人工档有挂着的报警当场弹窗、切回自动收窗——不等 500ms 拍。
        GrillAlarmResetModeHub.Changed += OnGrillResetModeChanged;
        // 报警总开关切换广播（V0.0.90）：切【关】立即收敛现存报警（红灯当场
        // 灭/复位窗即清），不等心跳拍；切【开】零动作（补报由状态机兜底）。
        GrillAlarmEnabledHub.Changed += OnGrillAlarmSwitchChanged;

        // 单线过账装配（P3.2 后半）：读 [CuringMes] 配置 → 建过账服务 →
        // 顶栏状态随 PostingChanged 刷新（后台线程回调，UiInvoke 封送）。
        var postOptions = CuringMesOptions.Load();
        _poster = new CuringMesPoster(postOptions,
            () => WorkOrderStore.Load(), log: m => LocalLogService.Write(m));
        _poster.PostingChanged += t => UiInvoke(() => PostingText = t);
        CuringMesPosterHolder.Default = _poster; // 工单窗上传动作的全局访问点
        ApplyPostOptions(postOptions);

        // 启动装配：读点表 + 工艺策略（都自愈）→ 总线 Start + 状态机
        var config = LoadOrDefault();
        _config = config;   // 记住当前点表（重连按钮复用，勿传 null）
        BuildSlots(config);
        // 扫码枪 + 库位绑定（P3.3）：读绑定簿 → 建会话 → 后台启动扫码枪（串口/TCP 按配置）。
        // 放在总线启动前——首拍刷新就能把已存绑定的产品码挂到卡片上。
        InitScanner();
        LoadPolicyBook();
        // 演示开关初始态：与 StartBus 同源读 ini——顶栏按钮状态与实际总线类型一致
        IsSimulate = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";
        SimulateText = IsSimulate ? "演示：开" : "演示：关";
        StartBus(config);
        _refreshTimer.Start();
    }

    // ---------- 扫码枪 / 库位绑定（P3.3 扫码工序） ----------

    /// <summary>
    /// 扫码装配：读 [Scanner] 配置 + 库位绑定簿（都自愈）→ 建绑定会话 →
    /// 后台启动扫码枪（串口/TCP 按配置自动选——串口 WMI 找口/开串口、TCP 后台
    /// 连接线程，都在子模块内部起线程，UI 线程禁碰——铁律 2）。
    /// 未启用/未配目标时只把状态条置提示文案，程序其余功能零影响。
    /// </summary>
    private void InitScanner()
    {
        // 缺键自愈落盘（V0.0.81）：config.ini 里没写的 [Scanner] 键按出厂缺省
        // （现场口径）补写进文件——文件所见即程序所得，现场核对/备份/重部署
        // 都有字面值，不再有"内存里有缺省、文件里空白"的两本账（幂等，已写
        // 的键一律不碰）。主窗每次启动装配先校一遍；配置窗保存本来就走
        // Save() 全量落盘，与此不冲突。
        ScannerOptions.EnsureDefaultsPersisted();
        _scannerOptions = ScannerOptions.Load();

        var reasons = new List<string>();
        var book = SlotBindingStore.Load(reasons);
        foreach (var reason in reasons)
            LocalLogService.Write(reason);

        _bindings = new SlotBindingSession(
            book,
            timeoutSeconds: _scannerOptions.NormSessionTimeoutSeconds,
            log: m => LocalLogService.Write(m));
        _bindings.Changed += OnBindingsChanged;

        _scanner = new ScannerBarcodeService(_scannerOptions);
        _scanner.BarcodeScanned += OnBarcodeScanned;
        _scanner.ConnectionChanged += OnScannerConnectionChanged;

        RefreshScanState();
        // 未启用/未配目标：不启动收码线程（状态条已给配置指引）。此前这里完全
        // 静默——Enabled=0 连一行日志都没有，现场"胶囊=未启用 + 日志无提示"
        // 排障只能去对配置文件（2026-11-07 现场实测踩坑）。现在补记一行跳过
        // 原因（与 Start() 本会写的文案同源），启动日志即可自助定位。
        if (_scannerOptions.SkipReasonText() is { } skip)
        {
            LocalLogService.Write(skip);
            return;
        }

        // 捕获局部实例（不读字段）：配置窗保存会换新实例，旧启动任务不能误启新枪。
        var scanner = _scanner;
        Task.Run(() =>
        {
            lock (_scannerLifecycleGate)
                scanner.Start();
        }).ContinueWith(_ => UiInvoke(RefreshScanState));
    }

    /// <summary>
    /// 扫码枪配置窗入口（dev 专属）：保存成功→按新参数热重建扫码枪（不重启程序）。
    /// 主窗统一提示"已保存"（与点表/策略窗同款）。
    /// </summary>
    private void OpenScannerConfig()
    {
        if (!Gate(OperatorRole.Dev, "扫码枪配置"))
            return;
        if (!_dialogs.ShowScannerConfig())
            return;
        ApplyScannerOptions();
        _dialogs.ShowMessage("扫码枪配置已保存并生效。", "保存成功");
    }

    /// <summary>
    /// 按盘上新配置热重建扫码枪（V0.0.77 建；V0.0.78 双模式同链）：
    ///   ① 绑定簿原样带进新会话（在架绑定不丢；等待产品码阶段重置为等库位码）；
    ///   ② 旧服务 UI 线程退订，后台串行"关旧连接→按新参数开新连接"（同一把锁防
    ///      新旧实例竞争；串口独占与 TCP connect 都锁在后台线程）；
    ///   ③ 立即刷扫码胶囊与卡片（不等 500ms 拍）。
    /// 配置全是低频项，重建开销（串口=WMI 找口+开串口几百 ms / TCP=起后台连接
    /// 线程立即返回）在后台，UI 无感。
    /// </summary>
    private void ApplyScannerOptions()
    {
        var options = ScannerOptions.Load();
        _scannerOptions = options;

        var old = _scanner;
        if (old != null)
        {
            old.BarcodeScanned -= OnBarcodeScanned;
            old.ConnectionChanged -= OnScannerConnectionChanged;
        }

        var book = _bindings.Snapshot();
        _bindings.Changed -= OnBindingsChanged;
        _bindings = new SlotBindingSession(
            book,
            timeoutSeconds: options.NormSessionTimeoutSeconds,
            log: m => LocalLogService.Write(m));
        _bindings.Changed += OnBindingsChanged;

        var fresh = new ScannerBarcodeService(options);
        fresh.BarcodeScanned += OnBarcodeScanned;
        fresh.ConnectionChanged += OnScannerConnectionChanged;
        _scanner = fresh;

        RefreshScanState();
        RefreshSlots();
        LocalLogService.Write("扫码枪配置已保存：按新连接方式后台重启扫码枪");

        Task.Run(() =>
        {
            lock (_scannerLifecycleGate)
            {
                old?.Dispose();   // 先关旧连接（串口最坏等心跳 3 秒 / TCP 关 socket），再开新的
                if (options.SkipReasonText() is { } skip)
                    LocalLogService.Write(skip);   // 新配置不启动：补一行原因，防日志层只剩"按新连接方式重启"一句造成误解
                else
                    fresh.Start();
            }
        }).ContinueWith(_ => UiInvoke(RefreshScanState));
    }

    /// <summary>
    /// 读码（子模块后台线程 → 封送回 UI）：交给会话解释成"扫库位/绑产品"。
    /// 扫错库位码给 3 秒浮层提示（日志同时留一行），不改变会话阶段。
    /// </summary>
    private void OnBarcodeScanned(object? sender, string code) => UiInvoke(() =>
    {
        var outcome = _bindings.Handle(code);
        if (outcome.Kind == ScanOutcomeKind.InvalidLocation)
        {
            _scanHintText = outcome.Message;
            _scanHintUntil = DateTime.Now.AddSeconds(3);
        }
        RefreshScanState();
    });

    /// <summary>连接边沿（后台线程 → 封送）：只刷状态条；连接/断开的日志由子模块统一出。</summary>
    private void OnScannerConnectionChanged(object? sender, bool connected) =>
        UiInvoke(RefreshScanState);

    /// <summary>绑定簿变化（会话阶段切换/绑定增删）：状态条 + 卡片立即刷新，不等 500ms 拍。</summary>
    private void OnBindingsChanged()
    {
        RefreshScanState();
        RefreshSlots();
    }

    /// <summary>
    /// 扫码胶囊唯一写点（阶段/未绑定报警/连接态三档 + 3 秒错误浮层覆盖）：
    /// 优先级 未启用 &gt; 未配置 &gt; 待扫产品码 &gt; 未绑定报警 &gt; 连接态。
    /// </summary>
    private void RefreshScanState()
    {
        ScanConnected = _scanner?.IsConnected == true;
        ScanWaitingProduct = _bindings.Phase == SlotBindingPhase.WaitingProduct;

        if (!_scannerOptions.Enabled)
        {
            ScanWarning = false;
            ScanText = "扫码：未启用";
            ScanTip = "扫码枪未启用。现场启用：设置→扫码枪配置（dev 专属）勾选启用并配好连接方式；" +
                      "或 config.ini [Scanner] 设 Enabled=1（TCP 方式配 TcpIp/TcpPort，串口方式填 " +
                      "PortName 或 DeviceKeyword），改 ini 需重启程序";
        }
        else if (!_scannerOptions.HasPortTarget)
        {
            ScanWarning = false;
            // 提示按接入方式说明缺哪一项（Tcp=TcpIp 空 / Serial=PortName+Keyword 空）。
            bool tcpMode = _scannerOptions.NormMode == "Tcp";
            ScanText = tcpMode ? "扫码：未配置 TCP 目标" : "扫码：未配置串口";
            ScanTip = tcpMode
                ? "config.ini [Scanner] 的 TcpIp 为空。在设置→扫码枪配置填枪侧 DTU 的 IP 与 " +
                  "TCP 端口后保存（保存即生效）；或手工改 ini 后重启程序"
                : "config.ini [Scanner] 的 PortName 与 DeviceKeyword 都为空。在设置→扫码枪配置填 " +
                  "PortName（设备管理器里的 COM 号）或 DeviceKeyword（设备名关键字）后保存；" +
                  "或手工改 ini 后重启程序";
        }
        else if (ScanWaitingProduct)
        {
            ScanWarning = false;
            ScanText = _bindings.Prompt;   // "A01 待扫产品码"
            ScanTip = "已扫库位码，请扫产品二维码完成绑定。扫错库位可直接扫另一个库位码切换，" +
                      "或点【取消】回到请扫库位码";
        }
        else if (_unboundTrayCount > 0)
        {
            ScanWarning = true;
            ScanText = $"⚠ {_unboundTrayCount} 个在架托盘未绑产品";
            ScanTip = "托盘在架但库位没有绑定产品：请先扫库位码（A01~J12）、再扫产品码。" +
                      "只提示，不拦截固化流程";
        }
        else
        {
            ScanWarning = false;
            ScanText = ScanConnected ? _bindings.Prompt : "扫码枪未连接";
            // 未连接排查指引按接入方式给（Tcp=查网线/DTU；Serial=查转接线/COM 口）。
            bool tcpMode = _scannerOptions.NormMode == "Tcp";
            ScanTip = ScanConnected
                ? $"扫码枪已连接（{_scanner?.Name ?? "扫码枪"}）：先扫库位码 A01~J12，再扫产品码；" +
                  $"{_scannerOptions.NormSessionTimeoutSeconds} 秒未扫产品码自动回到请扫库位码"
                : tcpMode
                    ? "扫码枪未连接：检查网线与枪侧 DTU（TCP 目标 IP:端口，枪/DTU 断电重启后确认 " +
                      "TCP-SERVER 配置还在）；服务每 3 秒后台自动重连，网络恢复即接通"
                    : "扫码枪未连接：检查 USB 转接线与 COM 口配置；服务每 3 秒后台自动重连，插回即恢复";
        }

        // 3 秒错误浮层（扫错码提示）：只覆盖文字，不动点色/按钮状态。
        if (_scanHintUntil > DateTime.Now)
            ScanText = _scanHintText;
    }

    // 点表读取：文件没了/空表 → 建缺省模板并立即落盘（现场第一次开机就有东西看）。
    // V0.0.41 起按现场实况生成：一台机台 10 列 × 上中下 3 层，每列 1 台町洋
    // GFNB-1A3A（IP 192.168.1.20~.29 现场现值预填）——自愈出来即是现场布局，
    // 配置窗打开核对/微调即可，不用从单台旧口径重配。
    private PointTableConfig LoadOrDefault()
    {
        var config = IoPointTableStore.Load();
        if (config == null)
        {
            config = IoPointTableStore.BuildMachineTemplate();
            IoPointTableStore.Save(config);
            _dialogs.ShowMessage(
                "点表配置为空，已按现场布局生成缺省模板并保存：" +
                "10 台耦合器（IO耦合器1~10，IP 192.168.1.20~.29），每台管一列的上中下 3 层。\n" +
                "菜单可打开点表配置窗核对（测试连接/批量生成/逐点改绑）。",
                "配置自愈");
        }
        return config;
    }

    /// <summary>
    /// 工艺策略装载：缺/坏 → 缺省库落盘自愈（静默不弹框——策略有缺省值可直接跑，
    /// 与点表"要用户改 IP"不同，不打扰操作员；自愈原因进操作日志）。
    /// 激活名同步到顶栏 PolicyText；随后做一次"在制固化时长单向上调"热更收口
    /// （V0.0.58，见 SlotCuringMachine.ApplyPolicyHotChange）——策略窗保存返回
    /// 即把新时长应用到进行中的仓位（只延不缩），不等每个仓位自然结续到点。
    /// </summary>
    private void LoadPolicyBook()
    {
        var reasons = new List<string>();
        var book = CuringPolicyStore.Load(reasons: reasons);
        if (book == null)
        {
            book = CuringPolicyStore.BuildDefaultBook();
            CuringPolicyStore.Save(book); // 自愈落盘（现场删了配置文件也能一键回来）
            foreach (var r in reasons)
                LocalLogService.Write(r);
        }
        _policyBook = book;
        RefreshPolicyText();
        _machine?.ApplyPolicyHotChange();   // 在制单向热更收口（启动时无在制，幂等）
    }

    /// <summary>顶栏策略段：激活名 + 全局时长（随显示单位时/分换口径；
    /// LoadPolicyBook 装载与 OnDisplayUnitChanged 单位切换双入口）。</summary>
    private void RefreshPolicyText()
    {
        var active = _policyBook?.ResolveActive();
        PolicyText = active == null ? "策略：无"
            : $"策略：{active.Name} · 全局{DurationUnit.Text(active.GlobalMinutes)}";
    }

    /// <summary>状态机取当前激活策略（每次托盘到位现取——策略窗激活后热切换生效）。</summary>
    private CuringPolicy? GetActivePolicy() => _policyBook?.ResolveActive();

    /// <summary>
    /// 仓位网格骨架：架×层全组合铺满（占位结构来自点表绑定，缺省 10 架 3 层=30 仓位）。
    /// 排布口径（与现场机台实景一致）：三排=上中下三层，一排 10 站——集合按
    /// "行优先"生成（先层后架），UniformGrid（Columns=RackCount/Rows=LevelCount）
    /// 逐格填充即得三排十站布局；卡片主标题用客户库位编码（SlotLocationCode）。
    /// 双架（V0.0.20）：全局架号每 10 站切一组（1~10=1号架、11~20=2号架），
    /// 主窗按组上下堆叠各铺 10×3；平铺 Slots 顺序不变（回归锚点）。
    /// 注意：顺序是布局契约，改前先看 MainWindow.xaml 的 UniformGrid 与
    /// MainVm_Slots_RowMajorOrder 用例——乱序会导致"层"错行显示。
    /// </summary>
    private void BuildSlots(PointTableConfig config)
    {
        // 架号/层数列取最大绑定值（点表多大网格多大；没绑定时按客户编码表 10×3 缺省）
        int racks = Math.Max(SlotLocationCode.RacksPerRow, config.Devices.SelectMany(d => d.Bindings)
            .Where(b => b.Rack > 0).Select(b => b.Rack).DefaultIfEmpty(SlotLocationCode.RacksPerRow).Max());
        int levels = Math.Max(SlotLocationCode.Levels, config.Devices.SelectMany(d => d.Bindings)
            .Where(b => b.Level > 0).Select(b => b.Level).DefaultIfEmpty(SlotLocationCode.Levels).Max());

        var all = new Dictionary<(int Rack, int Level), RackSlotViewModel>();
        for (int level = 1; level <= levels; level++)
        for (int rack = 1; rack <= racks; rack++)
        {
            var slot = new RackSlotViewModel { Rack = rack, Level = level };
            Slots.Add(slot);
            all[(rack, level)] = slot;
        }
        RackCount = racks;
        LevelCount = levels;

        // 分组视图：全局架号每 RacksPerRow(10) 站一台架，组内行优先（同平铺契约）。
        // 组数>1 才显示标题条——单架现场主窗与旧版视觉完全一致。
        RackGroups.Clear();
        int groupCount = (racks + SlotLocationCode.RacksPerRow - 1) / SlotLocationCode.RacksPerRow;
        for (int g = 1; g <= groupCount; g++)
        {
            int start = (g - 1) * SlotLocationCode.RacksPerRow + 1;
            int end = Math.Min(g * SlotLocationCode.RacksPerRow, racks);
            var groupSlots = new List<RackSlotViewModel>();
            for (int level = 1; level <= levels; level++)
            for (int rack = start; rack <= end; rack++)
                groupSlots.Add(all[(rack, level)]);
            RackGroups.Add(new RackGroupViewModel(
                $"{g}号固化架", groupSlots, levels, groupCount > 1));
        }
        GroupCount = groupCount;
    }

    private void StartBus(PointTableConfig? config)
    {
        lock (_busLock)
        {
            if (config == null || config.Devices.Count == 0)
            {
                ConnectionText = "未配置设备";
                ConnectionDetailText = "未配置设备";
                return;
            }
            var validation = PointTableValidator.Validate(config);
            if (!validation.Ok)
            {
                // 点表坏了照样起（模拟模式兜底），现场在配置窗修正
                string errors = string.Join("；", validation.Errors);
                ConnectionText = "点表有误";
                ConnectionDetailText = errors;
                _dialogs.ShowMessage(string.Join("\r\n", validation.Errors), "点表校验失败");
                return;
            }

            // 演示开关：config.ini [setting] Simulate=1 → Mock 总线（无硬件全流程），
            // key 与主程序 CimGateway（原 PlcMesBridge）同名沿用（跨程序一致的运维习惯）。
            bool simulate = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";
            IPointTableBus bus = simulate
                ? new MockPointTableBus
                {
                    // 演示光栅电平跟随现场极性配置（V0.0.72）：勾"信号取反"后
                    // 演示剧本发"被挡=OFF"，与状态机判定同口径。
                    GrillPolarityProvider = () => GrillSignalPolarityHub.Current,
                }
                : new ModbusPointTableBus();
            bus.Start(config);

            // 状态机接线（全部后台线程——铁律 2）：边沿直转（machine 内部有锁）；
            // 断连恢复对齐快照（托盘在架的仓位重新计时）；连接态/错误照旧回 UI。
            // 三事件回调统一比对 sender==_bus——总线热切换（重连/演示开关）时旧总线
            // Dispose 后残留线程仍可能晚发事件（连接超时兜圈超过停机等待上限），
            // 不比对会把新总线的顶栏文本/状态机喂上旧总线的报错与边沿（实测踩中）。
            bus.PointChanged += (s, e) =>
            {
                if (ReferenceEquals(s, _bus))
                    _machine?.OnPointChanged(e);
            };
            bus.ConnectionChanged += (s, connected) =>
            {
                if (!ReferenceEquals(s, _bus))
                    return;
                if (connected)
                    _machine?.AlignFromSnapshot();
                UiInvoke(() => SetConnectionTexts(connected, bus.ConnectionSummary));
            };
            bus.Error += (s, msg) => UiInvoke(() =>
            {
                if (ReferenceEquals(s, _bus))
                    ConnectionDetailText = msg;   // 详情全文进悬停；顶栏短词只跟连通态
            });
            // 顶栏连接段只显短词（V0.0.37：连接超时/异常详情一律悬停 Tooltip 展开，
            // 栏面不再被"连接超时（3000ms），请检查…"这类长文挤爆）。

            // 状态机：首次创建（带总线），重连只换通道（Attach，流程状态不丢）。
            // 顺序刻意"先建/换妥新通道、再停旧总线"——状态机中途不会摸到已释放对象。
            if (_machine == null)
            {
                _machine = new SlotCuringMachine(bus, GetActivePolicy,
                    log: m => LocalLogService.Write(m),
                    // 日志时长文本随显示单位（策略窗切小时 → 日志"开始固化计时 2小时"；
                    // 分钟口径保持"X 分钟"老字样）——LogText 是纯函数，后台线程安全。
                    durationText: DurationUnit.LogText,
                    // 报警复位模式每拍现取（V0.0.69）：策略窗光栅条切人工/自动，
                    // 下一边沿/心跳即生效（Hub 只读静态枚举字段，后台线程读安全）。
                    resetModeProvider: () => GrillAlarmResetModeHub.Current,
                    // 光栅信号极性每拍现取（V0.0.72）：策略窗光栅条选常开/常闭
                    // 下一边沿/心跳即生效（现场被挡=OFF 时选常闭，同上后台读安全）。
                    grillPolarityProvider: () => GrillSignalPolarityHub.Current,
                    // 报警总开关每拍现取（V0.0.90）：策略窗"光栅报警"开关，
                    // false=整条报警链静默（被挡不报/已挂的立即解除），门控不受影响。
                    grillAlarmEnabledProvider: () => GrillAlarmEnabledHub.Current);
                // 过账挂点（P3.2 后半）：状态机三事件 → 过账服务（只在创建时订阅
                // 一次——重连 Attach 换通道不重复订阅）。事件在状态机锁内触发，
                // Poster 方法内部 Task.Run 立即返回，不重入不阻塞。
                _machine.TrayArrived += (rack, level) => _poster?.OnTrayArrived(rack, level);
                _machine.CuringStarted += (rack, level) => _poster?.OnCuringStarted(rack, level);
                _machine.TrayLeft += (rack, level, phase, sec) =>
                {
                    _poster?.OnTrayLeft(rack, level, phase, sec);
                    // 取件=产品已离开库位 → 绑定作废（下次入库必须重新扫码）。
                    // 状态机事件在 _sync 锁内触发：落盘/刷新一律 Post 回 UI，不在这里做 IO。
                    UiInvoke(() =>
                    {
                        if (_bindings.Remove(rack, level))
                            LocalLogService.Write(
                                $"库位 {SlotLocationCode.EncodeInRack(rack, level)} 托盘离开，产品绑定已清除");
                    });
                };
            }
            else
                _machine.Attach(bus);
            _machine.AlignFromSnapshot(); // 首启/点表变更后按当前快照对齐

            var oldBus = _bus;
            _bus = bus;
            oldBus?.Dispose();
            SetConnectionTexts(_bus.IsConnected, _bus.ConnectionSummary);
        }
    }

    private void RefreshSlots()
    {
        var snapshot = _bus?.LatestSnapshot;
        // 空闲卡"计划 X"预览（V0.0.58）：激活策略每拍现取 + 仓位三级解析
        // （仓位>层>全局）——策略窗热更后下一拍（≤500ms）空闲卡立见新时长。
        var planPolicy = GetActivePolicy();

        // 扫码会话超时（P3.3）：扫了库位码走开 → 超时自动回"请扫库位码"，不让界面挂着旧库位误导。
        _bindings.Tick();

        int unboundTray = 0;   // 托盘在架但未绑定产品的库位数（扫码胶囊报警依据）
        foreach (var slot in Slots)
        {
            var status = _machine?.GetStatus(slot.Rack, slot.Level);
            var binding = _bindings.Get(slot.Rack, slot.Level);
            // 未绑定报警（客户口径只提示不拦截）：只要托盘在架就必须已绑产品。
            // 日志只记边沿——新出现的未绑定写一行，绑上/托盘离开后自动解除。
            bool missing = snapshot?.Get(new(true, IoPointRole.TrayDetect, slot.Rack, slot.Level)) == true
                           && binding == null;
            if (missing)
            {
                unboundTray++;
                if (_unboundAlerted.Add((slot.Rack, slot.Level)))
                    LocalLogService.Write(
                        $"库位 {SlotLocationCode.EncodeInRack(slot.Rack, slot.Level)} 托盘在架但未绑定产品，" +
                        "请先扫库位码再扫产品码（只提示，不拦截固化流程）");
            }
            else
            {
                _unboundAlerted.Remove((slot.Rack, slot.Level));
            }

            slot.Refresh(snapshot ?? PointTableSnapshot.Empty, snapshot != null, status,
                planPolicy?.ResolveMinutes(slot.Rack, slot.Level) ?? 0,
                binding?.ProductCode ?? "", missing);
        }
        _unboundTrayCount = unboundTray;

        // 统计条 + 工单带同拍刷（挂在同一 500ms tick 内，不另起定时器；UI 线程）。
        RefreshStats();
        RefreshWorkOrder();
        // 报警复位弹窗清单热更（V0.0.69）：每拍喂状态机当前报警清单——
        // presenter 内部按复位模式自行取舍（人工档差分行 + 弹窗决策，
        // 自动档清空不弹）；新报警窗开着就热进出，无需关了重开（用户拍板）。
        RefreshAlarmList();
        // 扫码胶囊同拍兜底（连接态/超时/hint 过期都在这里自然收敛）。
        RefreshScanState();
    }

    /// <summary>
    /// 报警清单推送（AlarmPresenter.Refresh 唯一喂点）：全部调用方（500ms 拍 /
    /// 复位完成回刷 / 模式切换广播）都在 UI 线程——Refresh 内部操作
    /// ObservableCollection（线程亲和），勿从后台线程直调。
    /// </summary>
    private void RefreshAlarmList() =>
        AlarmPresenter.Refresh(_machine?.GetAlarmSlots() ?? []);

    // ---------- 光栅报警人工复位事件链（V0.0.69，订阅在 ctor/Drop 在 Dispose） ----------

    private void OnAlarmShowRequested() =>
        _dialogs.ShowGrillAlarmReset(AlarmPresenter);

    private void OnAlarmCloseRequested() =>
        _dialogs.CloseGrillAlarmReset();

    /// <summary>单行复位请求：写灯是网络 IO（铁律 2）——后台线程跑状态机，完成后
    /// 回 UI 立即刷清单（点掉哪行哪行即走；即便复位 no-op 也回刷，幂等无害）。</summary>
    private void OnAlarmResetRequested(int rack, int level) =>
        Task.Run(() => _machine?.ManualResetGrillAlarm(rack, level))
            .ContinueWith(_ => UiInvoke(RefreshAlarmList));

    /// <summary>全部复位请求：后台串行逐项确认（锁内快照清单 + 逐项写灯），
    /// 防并发任务同时打总线；完成后回 UI 刷新。</summary>
    private void OnAlarmResetAllRequested() =>
        Task.Run(() =>
        {
            foreach (var (rack, level) in _machine?.GetAlarmSlots() ?? [])
                _machine?.ManualResetGrillAlarm(rack, level);
        }).ContinueWith(_ => UiInvoke(RefreshAlarmList));

    /// <summary>复位模式切换广播（策略窗按钮）：立即重新决策弹窗/收窗，不等下拍。</summary>
    private void OnGrillResetModeChanged() => RefreshAlarmList();

    /// <summary>
    /// 报警总开关切换广播（V0.0.90，策略窗"光栅报警"开关）：后台调状态机
    /// ApplyAlarmSwitch 收敛报警态——切【关】现存报警当场解除（写灯=网络 IO，
    /// 必须抛后台），完成后回 UI 立即刷报警复位清单（挂着的解除即收窗）；
    /// 切【开】零动作（重开后的被挡补报警由状态机 Pump 兜底，下一拍生效）。
    /// 事件在 UI 线程触发（Hub 线程纪律），网络动作一律 Task.Run。
    /// </summary>
    private void OnGrillAlarmSwitchChanged() =>
        Task.Run(() => _machine?.ApplyAlarmSwitch())
            .ContinueWith(_ => UiInvoke(RefreshAlarmList));

    /// <summary>
    /// 策略窗切显示单位（时↔分）的同步刷：卡片时长文本立即按新单位重写 +
    /// 统计条"⚡剩X"重拼。不动数值与总线/状态机（只字面换口径），UI 线程调用。
    /// </summary>
    private void OnDisplayUnitChanged()
    {
        foreach (var slot in Slots)
            slot.RefreshUnitText();
        RefreshStats();
        RefreshPolicyText();   // 顶栏"全局 X"段随显示单位换口径（V0.0.58）
    }

    private void OpenPointTable()
    {
        // 点表配置=开发者专属（V0.0.49：admin 也不会在下拉里见到此项）。
        if (!Gate(OperatorRole.Dev, "点表配置"))
            return;
        var saved = _dialogs.ShowPointTable();
        var config = IoPointTableStore.Load() ?? LoadOrDefault();
        _config = config;   // 点表可能已改，重连按钮要用新表
        BuildSlots(config);
        StartBus(config);
        if (saved)
            _dialogs.ShowMessage("点表已保存并生效。", "保存成功");
    }

    /// <summary>工艺策略窗：保存/激活后重读策略库（激活名可能已换，热切换生效）。</summary>
    private void OpenCuringPolicy()
    {
        if (!Gate(OperatorRole.Admin, "工艺策略"))
            return;
        var saved = _dialogs.ShowCuringPolicy();
        if (saved)
        {
            LoadPolicyBook();
            _dialogs.ShowMessage("工艺策略已保存并生效。", "保存成功");
        }
    }

    // ---------- 登录与权限门禁（P3.2） ----------

    /// <summary>
    /// 门禁统一出口：未登录先拦（提示登录），角色不够提示所需角色。
    /// 返回 true=放行。各功能所需角色（V0.0.49 三级口径）：工单·日志=操作员+ /
    /// 策略·IO测试·过账配置·演示·用户管理=管理员+ / 点表配置=开发者。
    /// </summary>
    private bool Gate(OperatorRole required, string what)
    {
        if (!_currentRole.HasValue)
        {
            _dialogs.ShowMessage("请先登录（顶栏『登录』按钮）。", "未登录");
            return false;
        }
        if (!OperatorRoleInfo.AtLeast(_currentRole, required))
        {
            _dialogs.ShowMessage(
                $"{what}需要 {OperatorRoleInfo.DisplayName(required)} 及以上，" +
                $"当前 {OperatorRoleInfo.DisplayName(_currentRole.Value)}。",
                "权限不足");
            return false;
        }
        return true;
    }

    /// <summary>登录窗：成功后记下操作员与角色（再点可换账号重登）。</summary>
    private void Login()
    {
        var result = _dialogs.ShowLogin();
        if (result == null)
            return;
        (_currentUserName, _currentRole) = result.Value;
        // 登录成功重置空闲基线：开程序晾了很久才登录的场景，若沿用开程序时刻
        // 的基线，登录后下一拍就"超时"被登出（IdleLogoutMonitor 契约）。
        _idleMonitor.NotifyActivity();
        // 顶栏格式"开发者：dev"——角色名开头（V0.0.49 三级口径，不带 LEVEL 字样）。
        OperatorText = $"{OperatorRoleInfo.DisplayName(_currentRole.Value)}：{_currentUserName}";
        LocalLogService.Write($"操作员登录：{_currentUserName}（{OperatorRoleInfo.ToText(_currentRole.Value)}）");
        // 设置按钮可用性/点表菜单项随角色刷新（operator 登录后设置按钮置灰、admin 点表项隐藏）。
        Raise(nameof(IsSettingsEnabled));
        Raise(nameof(SettingsToolTip));
        Raise(nameof(ShowPointTableItem));
        Raise(nameof(ShowScannerConfigItem));
    }

    /// <summary>
    /// 空闲超时自动退出（IdleLogoutMonitor.LogoutDue，V0.0.93）：静默登出——
    /// 不弹框（超时即人已离岗，弹框挂着没意义还可能与在开的功能窗叠加），
    /// 日志一行 + 顶栏回"未登录"；门禁即时收回——Gate/设置按钮可用性/菜单
    /// 显隐全随 _currentRole 判空，之后的操作要么被拦"请先登录"要么置灰。
    /// </summary>
    private void OnIdleLogout(int minutes)
    {
        if (!_currentRole.HasValue)
            return; // 未登录：监视器照跑（登录成功会重置基线），触发无语义直接忽略
        LocalLogService.Write($"空闲超时（{minutes} 分钟无操作），自动退出登录：{_currentUserName}");
        (_currentUserName, _currentRole) = ("", null);
        OperatorText = "未登录";
        // 与 Login() 对称的三件通知：设置按钮恢复可用、菜单项显隐回未登录口径。
        Raise(nameof(IsSettingsEnabled));
        Raise(nameof(SettingsToolTip));
        Raise(nameof(ShowPointTableItem));
        Raise(nameof(ShowScannerConfigItem));
    }

    /// <summary>空闲自动退出上限分钟（config.ini [setting] AutoLogoutMinutes）：
    /// 缺省 15；0/负数/非法文本一律=0 不启用。ctor 读一次缓存（provider 闭包
    /// 读字段），改 ini 重启生效（同 ScanIntervalMs 口径，非热调参数）。</summary>
    private static int ParseIdleMinutes()
    {
        var text = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "AutoLogoutMinutes", "15");
        return int.TryParse(text.Trim(), out var minutes) && minutes > 0 ? minutes : 0;
    }

    /// <summary>工单信息窗：登录即可开（窗内编辑区按角色再细分禁用）。</summary>
    private void OpenWorkOrder()
    {
        if (!Gate(OperatorRole.Operator, "工单信息"))
            return;
        var saved = _dialogs.ShowWorkOrder(_currentRole);
        if (saved)
        {
            // 工单窗刚落盘——主动重读一次（下部工单带立见新号，不等 500ms 下一拍；
            // LastWriteTime 戳变了 RefreshWorkOrder 自然重读，这里显式调只是不等待）。
            RefreshWorkOrder();
            _dialogs.ShowMessage("工单信息已保存。", "保存成功");
        }
    }

    /// <summary>用户管理窗：管理员+（admin 只能管操作员，dev 全管——窗内再按角色授权）。</summary>
    private void OpenAccounts()
    {
        if (!Gate(OperatorRole.Admin, "用户管理"))
            return;
        _dialogs.ShowAccounts(_currentRole!.Value);
    }

    /// <summary>
    /// IO 通讯测试窗：管理员+（现场接线的角色；比点表低一级——测试只动 IO 不改配置）。
    /// 共享当前总线与点表（null 拦下——总线还没启动时无连接可共享）。
    /// </summary>
    private void OpenIoTest()
    {
        if (!Gate(OperatorRole.Admin, "IO通讯测试"))
            return;
        if (_bus == null || _config == null)
        {
            _dialogs.ShowMessage("总线尚未启动（无连接可共享）。\n请先在点表配置窗保存点表并重连。", "IO通讯测试");
            return;
        }
        _dialogs.ShowIoTest(_bus, _config);
    }

    /// <summary>
    /// 顶栏过账状态与工单带显隐统一出口（ctor 初始化 + 过账配置窗保存热更共用，
    /// V0.0.47 收拢——原先 ctor 与 OpenPostConfig 各写一份"已启用（R 工位）"口径，
    /// 单工位改造只收了工单窗漏了主窗工单带）：
    /// ① PostingText——双工位带"（R 工位）"字样、单工位去工位字样（V0.0.38 口径）；
    /// ② PostingEnabled——工单卡"过账启用/过账未启用"角标；
    /// ③ MainSide + IsDualSide——工单带显隐（单工位只显 Side 侧一张卡，对齐工单窗）。
    /// </summary>
    private void ApplyPostOptions(CuringMesOptions options)
    {
        PostingText = options.Enabled ? (options.DualSide
            ? $"过账：已启用（{CuringMesOptions.NormSide(options.Side)} 工位）"
            : "过账：已启用")
            : "过账：未启用";
        PostingEnabled = options.Enabled;
        MainSide = CuringMesOptions.NormSide(options.Side);
        IsDualSide = options.DualSide;
    }

    /// <summary>
    /// 过账配置窗：管理员+。保存成功后热更新过账服务配置（不重启即生效）
    /// 并刷新顶栏状态。
    /// </summary>
    private void OpenPostConfig()
    {
        if (!Gate(OperatorRole.Admin, "过账配置"))
            return;
        var saved = _dialogs.ShowMesPostConfig();
        if (saved)
        {
            var options = CuringMesOptions.Load();
            _poster?.UpdateOptions(options);
            ApplyPostOptions(options);
            _dialogs.ShowMessage("过账配置已保存并生效。", "保存成功");
        }
    }

    /// <summary>
    /// 软件激活窗：管理员+（激活码是设备级凭据，操作员不应接触）。
    /// 激活成功/失败都刷新顶栏文案（关窗后状态可能变化）。
    /// </summary>
    private void OpenLicense()
    {
        if (!Gate(OperatorRole.Admin, "软件激活"))
            return;
        try
        {
            if (_dialogs.ShowLicenseActivation())
                _dialogs.ShowMessage("软件激活成功。", "软件激活");
        }
        finally
        {
            LicenseText = LicenseHost.ShortStatusText();
        }
    }

    /// <summary>
    /// 授权状态边沿（激活成功/换码切换/巡检到期）→ 顶栏文案即时刷新 +
    /// 操作日志一行（铁律 4 只记边沿）。LicenseHost 的边沿触发只来自 UI 线程
    /// 的 Refresh/TryActivate 调用点，直接赋值无需封送。
    /// </summary>
    private void OnLicenseStateChanged(LicenseState oldState, LicenseState newState)
    {
        LicenseText = LicenseHost.ShortStatusText();
        LocalLogService.Write("软件授权：" + LicenseText);
    }

    public void Dispose()
    {
        DurationUnit.Changed -= OnDisplayUnitChanged;   // 退订单位广播（防悬挂订阅）
        LicenseHost.StateChanged -= OnLicenseStateChanged;   // 退订授权状态边沿
        // 扫码枪退订 + 释放（P3.3）：子模块 Dispose 会等心跳线程退出（最坏 3 秒），
        // 放后台不卡关窗；串口句柄随进程退出由 OS 回收，无数据落盘风险。
        _bindings.Changed -= OnBindingsChanged;
        var scanner = _scanner;
        _scanner = null;
        if (scanner != null)
        {
            scanner.BarcodeScanned -= OnBarcodeScanned;
            scanner.ConnectionChanged -= OnScannerConnectionChanged;
            // 与配置热重启共用生命周期锁：退出释放排在"关旧/开新"之后，不抢串口。
            Task.Run(() =>
            {
                lock (_scannerLifecycleGate)
                    scanner.Dispose();
            });
        }
        // 报警复位链退订（V0.0.69）：四事件 + 模式广播；收掉开着的复位窗
        //（程序退出联动——窗是静态单例，不留悬挂 UI）。
        AlarmPresenter.ShowRequested -= OnAlarmShowRequested;
        AlarmPresenter.CloseRequested -= OnAlarmCloseRequested;
        AlarmPresenter.ResetRequested -= OnAlarmResetRequested;
        AlarmPresenter.ResetAllRequested -= OnAlarmResetAllRequested;
        GrillAlarmResetModeHub.Changed -= OnGrillResetModeChanged;
        GrillAlarmEnabledHub.Changed -= OnGrillAlarmSwitchChanged;   // 报警总开关退订（V0.0.90）
        _dialogs.CloseGrillAlarmReset();
        _refreshTimer.Stop();
        lock (_busLock)
        {
            _machine?.Dispose(); // 先停状态机心跳（不再写灯），再停总线
            _machine = null;
            _bus?.Dispose();
            _bus = null;
        }
    }

    // ---------- 顶部统计条 + 下部工单带（方案 D 信息带，2026-10-29） ----------

    /// <summary>
    /// 卡片集合 → 统计条：五态计数 + ⚡剩余最短。计数纯函数收在 Core 的
    /// SlotStatistics（可测，主 VM 只做搬运）；"⚡码·剩X"文本在此拼——剩余
    /// 数随显示单位走 DurationUnit（Core NextClose 只选最小项，不掺 UI 口径）。
    /// 500ms 同拍 + 单位切换广播双入口，UI 线程。
    /// </summary>
    private void RefreshStats()
    {
        IEnumerable<SlotStatItem> items = Slots.Select(s => new SlotStatItem(s.Kind, s.RemainMin, s.Code));
        var (idle, curing, gate, done, alarm) = SlotStatistics.Count(items);
        IdleCount = idle;
        CuringCount = curing;
        GateCount = gate;
        DoneCount = done;
        AlarmCount = alarm;
        var next = SlotStatistics.NextClose(items);
        NextCloseText = next == null ? "暂无计时" : $"⚡{next.Code}·剩{DurationUnit.Text(next.RemainMin)}";
    }

    /// <summary>
    /// 工单簿 → 底部信息带六件事（R/L 各：单号文本—空单号显示"—" / "已产/预设"文本 /
    /// 产出百分比 0~100——预设为 0 时 0%）。纯映射静态函数（测试锁口径不用起 VM）。
    /// </summary>
    public static (string RcNoR, string QtyR, int PctR, string RcNoL, string QtyL, int PctL)
        SummarizeWorkOrder(WorkOrderBook book)
    {
        static (string, string, int) One(WorkOrderSlot s) => (
            string.IsNullOrWhiteSpace(s.RcNo) ? "—" : s.RcNo,
            $"{Math.Max(0, s.ProducedQty)}/{Math.Max(0, s.PresetQty)}",
            s.PresetQty > 0
                ? Math.Clamp(100 * Math.Min(s.ProducedQty, s.PresetQty) / s.PresetQty, 0, 100)
                : 0);
        var r = One(book.Right);
        var l = One(book.Left);
        return (r.Item1, r.Item2, r.Item3, l.Item1, l.Item2, l.Item3);
    }

    // 工单带磁盘读节流：LastWriteTime 戳 + 首刷标记——同拍戳没变直接跳过读文件。
    private DateTime _workOrderStampUtc = DateTime.MinValue;
    private bool _workOrderLoaded;

    /// <summary>
    /// 刷下部工单带：文件"最后写时间戳"变了才 Load（避免 500ms 反复碰盘）；
    /// 缺文件按空工单兜底（WorkOrderStore 自愈语义）。工单窗保存后也会被
    /// OpenWorkOrder 主动调一次（不靠 500ms 兜底，操作员保存即见）。
    /// </summary>
    private void RefreshWorkOrder()
    {
        string path = WorkOrderStore.DefaultPath;
        try
        {
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_workOrderLoaded && stamp == _workOrderStampUtc)
                return;
            _workOrderStampUtc = stamp;
        }
        catch
        {
            // 取时间戳失败沿用上次状态（下一拍再试；文件 IO 出错不拖 UI 拍）
            return;
        }
        _workOrderLoaded = true;

        var book = WorkOrderStore.Load() ?? WorkOrderStore.BuildDefault();
        var (rcR, qtyR, pctR, rcL, qtyL, pctL) = SummarizeWorkOrder(book);
        WorkOrderRcNoR = rcR;
        WorkOrderQtyR = qtyR;
        WorkOrderPctR = pctR;
        WorkOrderRcNoL = rcL;
        WorkOrderQtyL = qtyL;
        WorkOrderPctL = pctL;
    }

    // ---------- 日志悬浮窗（顶栏【日志】；非模态单例由 DialogService 防重复） ----------

    /// <summary>日志/控制台悬浮窗：登录即可（只读不碰配置）。</summary>
    private void ShowLog()
    {
        if (!Gate(OperatorRole.Operator, "日志悬浮窗"))
            return;
        _dialogs.ShowLog(BuildSystemSnapshot());
    }

    /// <summary>
    /// 日志悬浮窗右侧"系统状态卡"快照（打开一刻定格）——顺手把当日日志目录
    /// 一并带上，现场找日志文件直接看路径。
    /// </summary>
    public LogSystemSnapshot BuildSystemSnapshot()
    {
        var rows = new List<SystemStatusRow>
        {
            new("连接状态", IsConnected ? "已连接" : "未连接"),
            new("连接详情", string.IsNullOrWhiteSpace(ConnectionDetailText) ? "—" : ConnectionDetailText),
            new("激活策略", _policyBook?.ResolveActive()?.Name ?? "无"),
            new("过账", PostingText),
            new("扫码枪", !_scannerOptions.Enabled ? "未启用"
                : ScanConnected ? $"已连接（{_scanner?.Name ?? "扫码枪"}）" : "未连接"),
            new("库位绑定", $"{_bindings.Count} 个（{_bindings.Prompt}）"),
            new("登录", OperatorText),
            new("仓位", $"{Slots.Count} 个（空闲{IdleCount} 计时{CuringCount} 待门控{GateCount} " +
                        $"放行{DoneCount} 红灯{AlarmCount}）"),
            new("日志目录", AppPaths.TodayLogDir()),
        };
        return new LogSystemSnapshot(DateTime.Now.ToString("HH:mm:ss"), rows);
    }

    // ---------- 双主题切换（Light=G2 浅色 / Dark=G1 深空） ----------

    /// <summary>
    /// 主题切换：目标名 → ThemeManager 换字典（UI 线程，DynamicResource 全窗生效）
    /// → 写回 ini（现场记住，下次启动同主题；写盘失败只丢记忆不回切）→
    /// 按钮文案翻新目标。切完记一笔操作日志（含主题名，方便现场回溯）。
    /// </summary>
    private void ToggleTheme()
    {
        var target = ThemeManager.ToggleTarget;
        ThemeManager.ApplyTheme(target);
        try
        {
            IniFile.Write(AppPaths.ConfigIni, "setting", "Theme", target);
        }
        catch
        {
            // 写盘失败不回切——界面已换，只是没记住（下次启动回旧主题）
        }
        ToggleThemeText = target == "Dark" ? "浅色" : "深色";
        LocalLogService.Write($"主题切换：{(target == "Dark" ? "深色（G1）" : "浅色（G2）")}");
    }

    // ---------- 演示模式开关（Mock 数据热切换，2026-10-30） ----------

    /// <summary>
    /// 演示模式开关：写回 [setting] Simulate 后立即经 StartBus 重建总线
    /// （Mock↔真机热切换，无需重启；状态机 Attach 换通道流程状态不丢，
    /// 快照对齐语义与"重新连接"完全同链）。门禁 L4——与点表/过账配置同级：
    /// 演示边沿会照常驱动状态机与过账链（Mock 托盘流转也会触发 MES 过账），
    /// 影响生产数据口径，不给 L3。ini 写失败时配置保持旧值，但内存态已翻转——
    /// 与主题切换"写盘失败只丢记忆"口径一致（下次启动按盘上值起）。
    /// </summary>
    private void ToggleSimulate()
    {
        if (!Gate(OperatorRole.Admin, "演示模式切换"))
            return;
        bool target = !IsSimulate;
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", target ? "1" : "0");
        IsSimulate = target;
        SimulateText = target ? "演示：开" : "演示：关";
        LocalLogService.Write(target
            ? "演示模式开启（Mock 总线，无真实硬件）"
            : "演示模式关闭（真机 Modbus 总线）");
        StartBus(_config);
    }
}
