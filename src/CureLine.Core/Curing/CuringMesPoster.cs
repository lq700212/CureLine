// =========================================================================
// CuringMesPoster：固化线单线过账服务（程序①，复刻 PLC_ReadData 老项目单机固化收料）
//
// 干什么：把仓位状态机的流程边沿映射成 MES 上报（复刻老项目寄存器命令字语义）：
//   托盘到位 → API0030 进站（ACTION=I，老项目"装载"）
//   托盘离开 → API0027 固化时间 + API0030 出站（ACTION=O，老项目"卸载/强制出料"）
//   工单窗手动 → API0032 物料上传（对照参考 HMI"批次物料上报"区）
//   出站被 MES 接受（STATUS=1）→ 工单已生产数量 +1 落盘。
//
// 与老项目的差异（架构决定，已在报价/设计文档登记）：
//   ① 触发源：老项目是机台寄存器命令字（D30001），新项目是 IO 耦合器点表边沿——
//     没有字符串寄存器读产品 ID 的通道，BC_NO 用工单的批次物料条码（批次级
//     过账而非板级）；W68 终版若要板级，走程序②整线网关。
//   ② 失败处理：老项目写机台报警位（D30013）+ 弹窗；新项目点表只有三色灯，
//     改为记 MES 日志 + 主窗顶栏过账状态文本（不打断程控，联调时按现场要求
//     再定是否要报警灯）。
//   ③ 无自动重传（老项目同款——失败人工处理，防重复过账）。
//
// 可配置口径（W68 终版未定 → 全配置化，见 CuringMesOptions 头注释）：
//   总开关 / 工位 / 固化时间单位 / 进站时机 / 中途取件是否出站 / 静态参数。
//   双架（V0.0.20）：自动进/出站按全局架号查 RackSides 映射定工位（未命中
//   回落 Side），MAC_NO/MAC_LOC_NO 随工位取（L 工位可选 MacNo_L/MacLocNo_L，
//   缺省回落主值）；物料上传不走映射（按钮工位红线，见 UploadMaterial）。
//
// 线程模型（铁律 2）：HTTP 全在后台 Task.Run——状态机在 _sync 锁内调本类
//   方法（事件处理器），方法体只投递不等待，绝不阻塞程控；工单读改写与
//   PostingChanged 事件都在后台线程，UI 订阅方必须封送。每个后台任务最外层
//   兜底 try-catch：过账任何异常都不许炸掉程控线程。
//
// 发送门槛（双保险）：总开关开 且 对应接口 URL 非空 才发——现场没配 MES
//   时不产生网络请求；开关关时记一条日志即过（可追溯）。
// =========================================================================

using Newtonsoft.Json;
using CureLine.Core.Mes;

namespace CureLine.Core.Curing;

public sealed class CuringMesPoster
{
    private CuringMesOptions _options; // 可变：UpdateOptions 热换引用（构造期无并发，直赋安全）
    private readonly Func<WorkOrderBook?> _workOrderProvider;
    private readonly Action<string> _log;

    /// <summary>
    /// 过账状态变化（顶栏展示文本，如「过账：14:00:01 进站 OK 架1层1」）。
    /// 后台线程触发，UI 订阅方必须 Dispatcher 封送（铁律 2）。
    /// </summary>
    public event Action<string>? PostingChanged;

    public CuringMesPoster(CuringMesOptions options,
        Func<WorkOrderBook?> workOrderProvider, Action<string>? log = null)
    {
        _options = options;
        _workOrderProvider = workOrderProvider;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// 热更新配置（过账配置窗保存后调）：引用读写原子，Volatile.Write 保证
    /// 后台任务下次读到新配置；进行中的任务用旧配置发完最后一单（无害）。
    /// </summary>
    public void UpdateOptions(CuringMesOptions options) =>
        System.Threading.Volatile.Write(ref _options, options);

    // ---------- 状态机边沿入口（状态机 _sync 锁内调，只投递不等待） ----------

    /// <summary>托盘到位（真实边沿；重启对齐不发——物料断电前已进站）。</summary>
    public void OnTrayArrived(int rack, int level) =>
        Task.Run(async () =>
        {
            try
            {
                if (_options.InAt != PostInAt.Arrive)
                    return; // Start 口径：进站挂开计时，到位不发
                await PostBoardInAsync(rack, level, "托盘到位").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"过账进站异常：{ex.Message}");
            }
        });

    /// <summary>开计时（真实边沿；重启续算不发——断电前已进站）。</summary>
    public void OnCuringStarted(int rack, int level) =>
        Task.Run(async () =>
        {
            try
            {
                if (_options.InAt != PostInAt.Start)
                    return; // Arrive 口径：进站已发过
                await PostBoardInAsync(rack, level, "开始计时").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"过账进站异常：{ex.Message}");
            }
        });

    /// <summary>
    /// 托盘离开（任何非空闲相位）。出站口径：
    ///   Done=正常取件（恒传）；Curing 中途取件=强制出料（PostAbortOut 控制）；
    ///   WaitingGate=进站了没开计时（Arrive 口径下补出站，有进必有出）。
    /// curedSeconds=已固化秒数（Done=计划时长；Curing=实际经过；WaitingGate=0）。
    /// </summary>
    public void OnTrayLeft(int rack, int level, SlotPhase leavePhase, int curedSeconds) =>
        Task.Run(async () =>
        {
            try
            {
                bool shouldPost = leavePhase switch
                {
                    SlotPhase.Done => true,
                    SlotPhase.Curing => _options.PostAbortOut,
                    // WaitingGate：进站已发（Arrive 口径）才补出站；Start 口径没发进站不发出站
                    SlotPhase.WaitingGate => _options.InAt == PostInAt.Arrive,
                    _ => false,
                };
                if (!shouldPost)
                {
                    _log($"架{rack}层{level} 托盘离开（{leavePhase}），按配置不上报出站");
                    return;
                }
                await PostBoardOutAsync(rack, level, leavePhase, curedSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"过账出站异常：{ex.Message}");
            }
        });

    // ---------- 工单窗手动动作 ----------

    /// <summary>
    /// 物料上传（API0032，对照 HMI"批次物料上报"：R/L 选工位 + 上传动作）。
    /// side 由工单窗按钮带出；使能关（工单 UploadEnabled）双保险拦下——
    /// 防绕过界面直调（同工单窗 Flush 兜底思想）。
    /// </summary>
    public void UploadMaterial(string side)
    {
        string s = CuringMesOptions.NormSide(side);
        Task.Run(async () =>
        {
            try
            {
                var book = _workOrderProvider();
                if (book == null || !book.UploadEnabled)
                {
                    Notify($"过账：{Now2()} {s}工位物料上传 失败（物料上传使能未开启）");
                    return;
                }
                string url = MesConfig.GetUrl("API0032");
                if (!CanPost(url, "物料上传"))
                    return;
                // 工位按按钮参数取（R/L 各有按钮），不走全局配置 Side——
                // 自动进/出站才用配置工位
                var slot = s == "L" ? book.Left : book.Right;
                if (slot == null || string.IsNullOrWhiteSpace(slot.RcNo))
                {
                    _log($"过账跳过（物料上传）：工单途程单号为空");
                    return;
                }
                var req = new MesModels.Api0032Request
                {
                    RC_NO = slot.RcNo,
                    BC_NO = book.BcNo,
                    PT_NO = book.PtNo1,
                    EMP_NO = book.EmpNo,
                    MAC_NO = StaticParam(_options.MacNo, "MAC_NO"),
                    MAC_LOC_NO = StaticParam(_options.MacLocNo, "MAC_LOC_NO"),
                    IN_DATETIME = Now(),
                    PLANT = StaticParam(_options.Plant, "PLANT"),
                };
                bool ok = await PostAsync(url, JsonConvert.SerializeObject(req), "物料上传")
                    .ConfigureAwait(false);
                Notify($"过账：{Now2()} {s}工位物料上传 {(ok ? "OK" : "失败（详见 MES 日志）")}");
            }
            catch (Exception ex)
            {
                _log($"物料上传异常：{ex.Message}");
            }
        });
    }

    // ---------- 上报链（后台线程执行） ----------

    /// <summary>进站：API0030 ACTION=I（BC_NO=工单批次物料条码，批次级过账）。
    /// 工位按架号查映射（RackSides，未命中回落 Side），机台参数随工位。</summary>
    private async Task PostBoardInAsync(int rack, int level, string why)
    {
        string url = MesConfig.GetUrl("API0030");
        if (!CanPost(url, "进站"))
            return;
        var book = _workOrderProvider();
        string side = _options.SideOfRack(rack);
        var slot = SideSlot(book, side);
        if (slot == null || string.IsNullOrWhiteSpace(slot.RcNo))
        {
            _log($"过账跳过（进站）：工单途程单号为空（{why}）");
            return;
        }
        var req = new MesModels.Api0030Request
        {
            RC_NO = slot.RcNo,
            ACTION = "I",
            BC_NO = book?.BcNo ?? "",
            PT_NO = book?.PtNo1 ?? "",
            MAC_NO = StaticParam(_options.MacNoOf(side), "MAC_NO"),
            MAC_LOC_NO = StaticParam(_options.MacLocNoOf(side), "MAC_LOC_NO"),
            IN_DATETIME = Now(),
            CHECK_RESULT = StaticParam(_options.CheckResult, "CHECK_RESULT"),
            PLANT = StaticParam(_options.Plant, "PLANT"),
        };
        bool ok = await PostAsync(url, JsonConvert.SerializeObject(req), $"进站[{side}]").ConfigureAwait(false);
        Notify($"过账：{Now2()} 进站[{side}] {(ok ? "OK" : "失败（详见 MES 日志）")} 架{rack}层{level} {why}");
    }

    /// <summary>
    /// 出站链：先 API0027 固化时间（外层数组，老项目顺序）后 API0030 出站；
    /// 出站被 MES 接受（STATUS=1）→ 工单已生产数量 +1 落盘（0027 失败只报警
    /// 不挡累计——出站才是过账完成标志）。
    /// </summary>
    private async Task PostBoardOutAsync(int rack, int level, SlotPhase leavePhase, int curedSeconds)
    {
        string url27 = MesConfig.GetUrl("API0027");
        string url30 = MesConfig.GetUrl("API0030");
        if (!CanPost(url27, "出站") || !CanPost(url30, "出站"))
            return;
        var book = _workOrderProvider();
        string side = _options.SideOfRack(rack);
        var slot = SideSlot(book, side);
        if (slot == null || string.IsNullOrWhiteSpace(slot.RcNo))
        {
            _log($"过账跳过（出站）：工单途程单号为空（架{rack}层{level}）");
            return;
        }

        // --- API0027 固化时间（外层数组；PRA_Value 单位按配置，缺省秒=老项目口径）---
        int timeValue = _options.TimeUnit == CuringTimeUnit.Minutes
            ? (int)Math.Round(curedSeconds / 60.0) : curedSeconds;
        var req27 = new MesModels.Api0027Request
        {
            RC_NO = slot.RcNo,
            PT_NO = book?.PtNo1 ?? "",
            MAC_NO = StaticParam(_options.MacNoOf(side), "MAC_NO"),
            MAC_LOC_NO = StaticParam(_options.MacLocNoOf(side), "MAC_LOC_NO"),
            PRA_CODE = StaticParam(_options.PraCode, "PRA_CODE"),
            PRA_Value = timeValue.ToString(),
            Collect_Datetime = Now(),
            PLANT = StaticParam(_options.Plant, "PLANT"),
        };
        bool ok27 = await PostAsync(url27, JsonConvert.SerializeObject(new[] { req27 }), $"固化时间[{side}]")
            .ConfigureAwait(false);
        Notify($"过账：{Now2()} 固化时间[{side}] {(ok27 ? "OK" : "失败（详见 MES 日志）")} " +
               $"架{rack}层{level} {leavePhase} 固化{curedSeconds}秒");

        // --- API0030 出站（ACTION=O）---
        var req30 = new MesModels.Api0030Request
        {
            RC_NO = slot.RcNo,
            ACTION = "O",
            BC_NO = book?.BcNo ?? "",
            PT_NO = book?.PtNo1 ?? "",
            MAC_NO = StaticParam(_options.MacNoOf(side), "MAC_NO"),
            MAC_LOC_NO = StaticParam(_options.MacLocNoOf(side), "MAC_LOC_NO"),
            IN_DATETIME = Now(),
            CHECK_RESULT = StaticParam(_options.CheckResult, "CHECK_RESULT"),
            PLANT = StaticParam(_options.Plant, "PLANT"),
        };
        bool ok30 = await PostAsync(url30, JsonConvert.SerializeObject(req30), $"出站[{side}]")
            .ConfigureAwait(false);

        // 出站被接受 → 已生产数量 +1 落盘（工单是现场作业状态，读-改-写；
        // 写者只有工单窗与本处，低频无并发热点，竞态窗口可接受）
        bool counted = false;
        if (ok30)
            counted = CountProduced(slot.RcNo);
        Notify($"过账：{Now2()} 出站 {(ok30 ? "OK" : "失败（详见 MES 日志）")} " +
               $"架{rack}层{level} {leavePhase}" + (counted ? "，已生产+1" : ""));
    }

    /// <summary>出站被接受后给对应工位的已生产数量 +1 并落盘（按途程单号定位工位）。</summary>
    private bool CountProduced(string rcNo)
    {
        try
        {
            var book = _workOrderProvider();
            if (book == null)
                return false;
            WorkOrderSlot? target =
                book.Right.RcNo == rcNo ? book.Right :
                book.Left.RcNo == rcNo ? book.Left : null;
            if (target == null)
                return false;
            target.ProducedQty++;
            WorkOrderStore.Save(book);
            return true;
        }
        catch (Exception ex)
        {
            _log($"已生产数量累计落盘失败：{ex.Message}");
            return false;
        }
    }

    // ---------- 发送与结果（对齐老项目 CheckMESResponse 口径） ----------

    /// <summary>发送门槛：开关开 + URL 非空。不过发时记原因日志（现场可追溯）。</summary>
    private bool CanPost(string url, string what)
    {
        if (!_options.Enabled)
        {
            _log($"过账跳过（{what}）：总开关未启用（过账配置窗开启）");
            return false;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            _log($"过账跳过（{what}）：接口 URL 未配置（过账配置窗填写）");
            return false;
        }
        return true;
    }

    /// <summary>POST + 解析应答（STATUS=1 成功；HTTP 失败/STATUS≠1/解析失败均算失败）。</summary>
    private async Task<bool> PostAsync(string url, string json, string what)
    {
        var (ok, resp) = await MesHttpClient.PostJsonAsync(url, json).ConfigureAwait(false);
        if (!ok)
        {
            MesLogger.WriteLog("Send FAIL", $"{what} 网络请求失败: {resp}", -1);
            return false;
        }
        try
        {
            var obj = JsonConvert.DeserializeObject<MesModels.BaseResponse>(resp);
            if (obj is { STATUS: not "1" })
            {
                MesLogger.WriteLog("Send FAIL", $"{what} 被MES拒绝: {obj.MSG}", -1);
                return false;
            }
        }
        catch
        {
            MesLogger.WriteLog("Send FAIL", $"{what} 报文解析失败: {resp}", -1);
            return false;
        }
        return true;
    }

    // ---------- 小工具 ----------

    /// <summary>指定工位的工单槽（自动过账按架号映射出的 side；物料上传按钮工位同款）。</summary>
    private static WorkOrderSlot? SideSlot(WorkOrderBook? book, string side) =>
        side == "L" ? book?.Left : book?.Right;

    /// <summary>静态参数：配置优先，空回退 MES 参数记忆（mes_params.json）。</summary>
    private static string StaticParam(string configured, string memoryField) =>
        !string.IsNullOrEmpty(configured) ? configured : MesParamStore.Get(memoryField);

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static string Now2() => DateTime.Now.ToString("HH:mm:ss");

    private void Notify(string text) => PostingChanged?.Invoke(text);
}
