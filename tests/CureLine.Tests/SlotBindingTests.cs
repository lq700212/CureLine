// =========================================================================
// SlotBindingTests：扫码绑定工序（P3.3；V0.0.99 三道拦截 + 防呆 + 去超时）
//   ① SlotBindingStore：绑定簿 JSON 存取 / 缺文件 / 坏文件自愈；
//   ② SlotBindingSession：三段式扫码状态机（扫库位 → 扫产品（多个）→ 再扫
//      库位=上料结束），覆盖清零 / 防呆拦截 / 在制库位拦截 / 放回件三道闸
//      （禁放回原库位、禁混装、续接口径确认）/ 取件清除；
//   ③ ScannerOptions：ini 缺省自愈与参数规范化；
//   ④ ScannerBarcodeService：事件转发（假 IScanner，不碰真实串口）。
// 全部用 TestScope 隔离临时目录，不污染仓库/本机配置。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;
using Kaleidoscope.Models;
using Kaleidoscope.Services;

namespace CureLine.Tests;

public class SlotBindingTests
{
    /// <summary>
    /// 建一个可注入假时钟的会话（persist 收集每次落盘快照，不碰磁盘）。
    /// takeOut/resume/occupied 三个注入点默认不给（=Core 独立可用口径）。
    /// </summary>
    private static (SlotBindingSession Session, List<SlotBindingBook> Saved, Func<DateTimeOffset, DateTimeOffset> SetNow)
        MakeSession(
            Func<string, SlotTakeOutInfo?>? takeOutLookup = null,
            Func<SlotReloadPrompt, SlotReloadChoice>? resumeDecider = null,
            Func<int, int, bool>? slotOccupiedProvider = null)
    {
        var now = new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.FromHours(8));
        var saved = new List<SlotBindingBook>();
        var session = new SlotBindingSession(
            new SlotBindingBook(),
            clock: () => now,
            persist: book => saved.Add(book),
            takeOutLookup: takeOutLookup,
            resumeDecider: resumeDecider,
            slotOccupiedProvider: slotOccupiedProvider);
        // 返回"改写当前时间"的委托（闭包捕获同一个 now 变量）。
        return (session, saved, value => now = value);
    }

    // ---------------- 会话状态机 ----------------

    [Fact(DisplayName = "扫码绑定_扫库位→扫产品→再扫库位=上料结束并落盘")]
    public void Session_LocationThenProductThenConfirm_EndsLoadAndPersists()
    {
        var (session, saved, _) = MakeSession();

        var first = session.Handle("A01");
        Assert.Equal(ScanOutcomeKind.LocationAccepted, first.Kind);
        Assert.Equal(SlotBindingPhase.WaitingProduct, session.Phase);
        Assert.Equal(1, session.PendingRack);
        Assert.Equal(1, session.PendingLevel);
        Assert.Contains("A01 待扫产品码", session.Prompt);
        Assert.Empty(saved);   // 还没绑产品，不落盘
        Assert.True(session.IsLoadUnconfirmed(1, 1));   // 上料未确认 → 该库位抑制光栅报警

        var second = session.Handle("  SN-2026-000123  ");
        Assert.Equal(ScanOutcomeKind.ProductBound, second.Kind);
        Assert.Equal("SN-2026-000123", second.ProductCode);   // 首尾空白已 Trim
        Assert.Equal(SlotBindingPhase.WaitingProduct, session.Phase);   // 还能继续扫产品

        var record = session.Get(1, 1)!;
        Assert.Equal(new[] { "SN-2026-000123" }, record.ProductCodes.ToArray());
        Assert.Equal("A01", record.LocationCode);
        Assert.Single(saved);                                  // 绑定成功立即落盘一次
        Assert.Equal("SN-2026-000123", saved[0].Bindings.Single().ProductCodes.Single());

        // 第三次：再扫同一个库位码 = 上料结束（二次确认）
        var third = session.Handle("A01");
        Assert.Equal(ScanOutcomeKind.LoadConfirmed, third.Kind);
        Assert.Equal(SlotBindingPhase.WaitingLocation, session.Phase);
        Assert.Equal("请扫库位码", session.Prompt);
        Assert.True(session.IsLoadConfirmed(1, 1));
        Assert.False(session.IsLoadUnconfirmed(1, 1));   // 上料已确认 → 恢复光栅报警
    }

    [Fact(DisplayName = "扫码绑定_一个库位可绑多个产品（逐个追加不清旧）")]
    public void Session_MultipleProducts_AppendedToSameSlot()
    {
        var (session, _, _) = MakeSession();
        session.Handle("A01");
        session.Handle("SN-1");
        session.Handle("SN-2");
        session.Handle("SN-3");

        var record = session.Get(1, 1)!;
        Assert.Equal(new[] { "SN-1", "SN-2", "SN-3" }, record.ProductCodes.ToArray());
        Assert.Equal(3, session.ProductCount(1, 1));
        Assert.Equal(3, session.TotalProductCount);
        // 上料未确认（还没二次扫库位码）
        Assert.True(session.IsLoadUnconfirmed(1, 1));
    }

    [Fact(DisplayName = "扫码绑定_同库位重复扫同一SN不重复入库")]
    public void Session_DuplicateProduct_Ignored()
    {
        var (session, _, _) = MakeSession();
        session.Handle("A01");
        session.Handle("SN-1");
        var again = session.Handle("SN-1");

        Assert.Equal(ScanOutcomeKind.DuplicateProduct, again.Kind);
        Assert.Equal(1, session.ProductCount(1, 1));
    }

    [Fact(DisplayName = "扫码绑定_上料确认事件抛给宿主（追溯库盖章用）")]
    public void Session_LoadConfirmed_RaisesEvent()
    {
        var (session, _, _) = MakeSession();
        var confirmed = new List<(int Rack, int Level)>();
        var bound = new List<(int Rack, int Level, string Sn, int Prior)>();
        session.LoadConfirmed += (rack, level) => confirmed.Add((rack, level));
        session.ProductBound += (rack, level, sn, prior) => bound.Add((rack, level, sn, prior));

        session.Handle("A01");
        session.Handle("SN-1");
        session.Handle("A01");

        Assert.Equal(new[] { (1, 1, "SN-1", 0) }, bound.ToArray());
        Assert.Equal(new[] { (1, 1) }, confirmed.ToArray());
    }

    [Fact(DisplayName = "扫码绑定_新扫库位码=新一轮上料（旧产品绑定清空）")]
    public void Session_ScanLocation_ClearsOldBindingImmediately()
    {
        var (session, saved, _) = MakeSession();
        session.Handle("B02");   // B02 = 架 2 层 1
        session.Handle("OLD-CODE");
        Assert.NotNull(session.Get(2, 1));

        // 上料结束（再扫同一库位码）后再次扫该库位 = 新一轮上料 → 旧绑定清掉
        // （客户口径：扫库位先清旧产品信息）
        session.Handle("B02");
        session.Handle("B02");
        Assert.NotNull(session.Get(2, 1));
        Assert.Empty(session.Get(2, 1)!.ProductCodes);
        Assert.Equal(SlotBindingPhase.WaitingProduct, session.Phase);
        Assert.DoesNotContain(saved[^1].Bindings, b => b.ProductCodes.Count > 0);
    }

    [Fact(DisplayName = "扫码绑定_上一个绑定未结束时扫别的库位码=拦截（防呆）")]
    public void Session_OtherLocationWhileWaitingProduct_Blocked()
    {
        var (session, _, _) = MakeSession();
        session.Handle("A01");
        var outcome = session.Handle("C03");

        // V0.0.99 防呆：不再自动切换——上一个绑定流程还没结束，必须先二次扫它。
        Assert.Equal(ScanOutcomeKind.LocationFlowNotFinished, outcome.Kind);
        Assert.Contains("绑定未结束", outcome.Message);
        // 会话仍停在原库位（A01），提示操作员先结束它
        Assert.Equal(SlotBindingPhase.WaitingProduct, session.Phase);
        Assert.Equal(1, session.PendingRack);
        Assert.Equal(1, session.PendingLevel);
        Assert.Contains("A01 待扫产品码", session.Prompt);
        // 拦截不产生新库位记录
        Assert.Null(session.Get(3, 1));
    }

    [Fact(DisplayName = "扫码绑定_二次扫原库位码后可再扫新库位码")]
    public void Session_AfterConfirming_CanScanNextLocation()
    {
        var (session, _, _) = MakeSession();
        session.Handle("A01");
        session.Handle("SN-1");
        Assert.Equal(ScanOutcomeKind.LoadConfirmed, session.Handle("A01").Kind);

        var next = session.Handle("C03");
        Assert.Equal(ScanOutcomeKind.LocationAccepted, next.Kind);
        Assert.Equal(3, session.PendingRack);
        Assert.Equal(1, session.PendingLevel);   // C03 = 架 3 层 1（level = 3-3+1）
        Assert.Contains("C03 待扫产品码", session.Prompt);
    }

    [Fact(DisplayName = "扫码绑定_在制库位不许再添新产品（固化时间必须统一）")]
    public void Session_CuringSlot_BlocksNewProduct()
    {
        // 状态机口径：托盘在架且已进入固化流程 → 该库位占用
        var (session, _, _) = MakeSession(slotOccupiedProvider: (rack, level) => rack == 1 && level == 1);
        // 先绑一件产品（占用判定要求"库里已有产品"才拦——没绑过的不拦）
        session.Handle("A01");
        session.Handle("SN-1");
        session.Handle("A01");   // 上料结束

        var outcome = session.Handle("A01");   // 再扫同一库位 = 往在制库位加货
        Assert.Equal(ScanOutcomeKind.SlotOccupiedBlocked, outcome.Kind);
        Assert.Contains("正在固化", outcome.Message);
        // 原绑定没被动过
        Assert.Equal(new[] { "SN-1" }, session.Get(1, 1)!.ProductCodes.ToArray());
    }

    [Fact(DisplayName = "扫码绑定_放回件不许放回原库位")]
    public void Session_ReturnToOrigin_Blocked()
    {
        var (session, _, _) = MakeSession(
            takeOutLookup: sn => sn == "SN-R" ? new SlotTakeOutInfo
            {
                ProductSn = "SN-R", Rack = 2, Level = 1, CuredMinutes = 480,
            } : null);

        session.Handle("B02");   // B02 = 架 2 层 1（=取出的原库位）
        var outcome = session.Handle("SN-R");

        Assert.Equal(ScanOutcomeKind.ReturnToOriginBlocked, outcome.Kind);
        Assert.Contains("不能放回本库位", outcome.Message);
        Assert.Equal(0, session.ProductCount(2, 1));
    }

    [Fact(DisplayName = "扫码绑定_放回新库位时弹窗问口径（续接=累计达标）")]
    public void Session_ReturnToNewSlot_AsksBasis_ResumeKeepsPriorMinutes()
    {
        SlotReloadPrompt? seen = null;
        var (session, _, _) = MakeSession(
            takeOutLookup: sn => sn == "SN-R" ? new SlotTakeOutInfo
            {
                ProductSn = "SN-R", Rack = 2, Level = 1, CuredMinutes = 480,
            } : null,
            resumeDecider: prompt => { seen = prompt; return SlotReloadChoice.Resume; });

        session.Handle("C03");   // 换到新库位
        var outcome = session.Handle("SN-R");

        Assert.Equal(ScanOutcomeKind.ProductBound, outcome.Kind);
        Assert.NotNull(seen);
        Assert.Equal("SN-R", seen!.ProductSn);
        Assert.Equal("C03", seen.LocationCode);
        Assert.Equal("B02", seen.PreviousLocationCode);
        Assert.Equal(480, seen.PriorMinutes);       // 之前已固化 8 小时
        // 续接口径：登记前段时长 → 库位本次计时 = 计划 − 480
        Assert.Equal(480, session.GetProductPriorMinutes(3, 1, "SN-R"));
        Assert.Equal(480, session.GetResumeBaselineMinutes(3, 1));
        Assert.Equal(SlotCureBasis.Resume, session.Get(3, 1)!.CureBasis);
    }

    [Fact(DisplayName = "扫码绑定_放回件选完全重新开始=当全新件（不进续接字典）")]
    public void Session_ReturnFresh_TreatedAsNew()
    {
        var (session, _, _) = MakeSession(
            takeOutLookup: sn => sn == "SN-R" ? new SlotTakeOutInfo
            {
                ProductSn = "SN-R", Rack = 2, Level = 1, CuredMinutes = 480,
            } : null,
            resumeDecider: _ => SlotReloadChoice.Fresh);

        session.Handle("C03");
        var outcome = session.Handle("SN-R");

        Assert.Equal(ScanOutcomeKind.ProductBound, outcome.Kind);
        Assert.Equal(0, session.GetProductPriorMinutes(3, 1, "SN-R"));
        Assert.Equal(0, session.GetResumeBaselineMinutes(3, 1));
        Assert.Equal(SlotCureBasis.Fresh, session.Get(3, 1)!.CureBasis);
    }

    [Fact(DisplayName = "扫码绑定_禁混装：续接口径的库位放不进全新产品")]
    public void Session_ResumeSlot_RejectsFreshProduct()
    {
        var (session, _, _) = MakeSession(
            takeOutLookup: sn => sn == "SN-R" ? new SlotTakeOutInfo
            {
                ProductSn = "SN-R", Rack = 2, Level = 1, CuredMinutes = 480,
            } : null,
            resumeDecider: _ => SlotReloadChoice.Resume);

        session.Handle("C03");
        session.Handle("SN-R");    // 续接件定档=Resume

        var outcome = session.Handle("SN-NEW");   // 再放一个全新件 → 混装拦截
        Assert.Equal(ScanOutcomeKind.CureBasisConflict, outcome.Kind);
        Assert.Contains("不能放入全新产品", outcome.Message);
        Assert.Equal(1, session.ProductCount(3, 1));
    }

    [Fact(DisplayName = "扫码绑定_禁混装：全新件先定档的库位放不进续接件")]
    public void Session_FreshSlot_RejectsResumeProduct()
    {
        var (session, _, _) = MakeSession(
            takeOutLookup: sn => sn == "SN-R" ? new SlotTakeOutInfo
            {
                ProductSn = "SN-R", Rack = 2, Level = 1, CuredMinutes = 480,
            } : null,
            // 操作员选"接着之前固化继续"→ 与库位已定档的"全新计时"冲突 → 拦截
            resumeDecider: _ => SlotReloadChoice.Resume);

        session.Handle("C03");
        session.Handle("SN-NEW");   // 全新件先定档=Fresh

        var outcome = session.Handle("SN-R");   // 续接件 → 混装拦截
        Assert.Equal(ScanOutcomeKind.CureBasisConflict, outcome.Kind);
        Assert.Contains("不能按续接累计放入", outcome.Message);
        Assert.Equal(1, session.ProductCount(3, 1));
    }

    [Fact(DisplayName = "扫码绑定_放回流程取消=不绑定（轻提示类别，非红线拦截）")]
    public void Session_ReturnCancelled_NotBound()
    {
        var (session, _, _) = MakeSession(
            takeOutLookup: sn => sn == "SN-R" ? new SlotTakeOutInfo
            {
                ProductSn = "SN-R", Rack = 2, Level = 1, CuredMinutes = 480,
            } : null,
            resumeDecider: _ => SlotReloadChoice.Cancel);

        session.Handle("C03");
        var outcome = session.Handle("SN-R");

        Assert.Equal(ScanOutcomeKind.ReloadCancelled, outcome.Kind);
        Assert.Contains("已取消放入", outcome.Message);
        Assert.Equal(0, session.ProductCount(3, 1));
    }

    [Fact(DisplayName = "扫码绑定_非库位码在等库位阶段被拒并保持原状态")]
    public void Session_InvalidLocation_StaysWaiting()
    {
        var (session, _, _) = MakeSession();
        var outcome = session.Handle("NOT-A-SLOT");

        Assert.Equal(ScanOutcomeKind.InvalidLocation, outcome.Kind);
        Assert.Equal(SlotBindingPhase.WaitingLocation, session.Phase);
        Assert.Contains("不是库位码", outcome.Message);
    }

    [Fact(DisplayName = "扫码绑定_空扫码直接忽略")]
    public void Session_EmptyCode_Ignored()
    {
        var (session, _, _) = MakeSession();
        Assert.Equal(ScanOutcomeKind.Ignored, session.Handle("   ").Kind);
        Assert.Equal(ScanOutcomeKind.Ignored, session.Handle(null).Kind);
        Assert.Equal(SlotBindingPhase.WaitingLocation, session.Phase);
    }

    [Fact(DisplayName = "扫码绑定_再扫同一库位码=上料结束（二次确认）")]
    public void Session_SameLocationAgain_ConfirmsLoadEnd()
    {
        var (session, _, _) = MakeSession();
        session.Handle("D05");
        var outcome = session.Handle("D05");
        Assert.Equal(ScanOutcomeKind.LoadConfirmed, outcome.Kind);
        Assert.Equal(SlotBindingPhase.WaitingLocation, session.Phase);
        Assert.Equal(0, session.PendingRack);              // 已回空闲（待绑目标清零）
        Assert.True(session.IsLoadConfirmed(4, 2));           // D05 = 架 4 层 2
    }

    [Fact(DisplayName = "扫码绑定_取消回到请扫库位码")]
    public void Session_Cancel_ResetsToWaitingLocation()
    {
        var (session, _, _) = MakeSession();
        session.Handle("F07");
        Assert.True(session.Cancel());
        Assert.Equal(SlotBindingPhase.WaitingLocation, session.Phase);
        Assert.False(session.Cancel());   // 本来就在等库位码：no-op
    }

    [Fact(DisplayName = "扫码绑定_托盘离开清除绑定")]
    public void Session_Remove_ClearsBinding()
    {
        var (session, _, _) = MakeSession();
        session.Handle("G08");
        session.Handle("SN-X");

        Assert.True(session.Remove(7, 2));   // G08 = 架 7 层 2
        Assert.Null(session.Get(7, 2));
        Assert.False(session.Remove(7, 2));  // 重复清除 no-op
    }

    [Fact(DisplayName = "扫码绑定_快照按架层升序且脱离内存对象")]
    public void Session_Snapshot_OrderedAndDetached()
    {
        var (session, _, _) = MakeSession();
        foreach (var code in new[] { "J12", "A01", "F07" })
        {
            session.Handle(code);
            session.Handle("SN-" + code);
            session.Handle(code);   // 上料结束（再扫同库位码）
        }

        var book = session.Snapshot();
        Assert.Equal(new[] { "A01", "F07", "J12" },
            book.Bindings.Select(b => b.LocationCode).ToArray());
        Assert.Equal(3, session.Count);

        // 快照是副本：改快照不影响内存
        book.Bindings[0].ProductCodes[0] = "TAMPERED";
        Assert.Equal("SN-A01", session.Get(1, 1)!.ProductCodes[0]);
    }

    // ---------------- 绑定簿存取 ----------------

    [Fact(DisplayName = "绑定簿_存取往返")]
    public void Store_SaveLoad_RoundTrip()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var book = new SlotBindingBook();
        book.Bindings.Add(new SlotBindingRecord
        {
            Rack = 1, Level = 1,
            ProductCodes = new List<string> { "SN-001", "SN-002" },
            BoundAt = new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.FromHours(8)),
            UpdatedAt = new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.FromHours(8)),
            LoadConfirmed = true,
            LoadConfirmedAt = new DateTimeOffset(2026, 11, 7, 9, 31, 0, TimeSpan.FromHours(8)),
        });
        book.Bindings.Add(new SlotBindingRecord
        {
            Rack = 10, Level = 3,
            ProductCodes = new List<string> { "SN-003" },
            BoundAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        });
        SlotBindingStore.Save(book);

        var loaded = SlotBindingStore.Load()!;
        Assert.Equal(2, loaded.Bindings.Count);
        Assert.Equal(new[] { "SN-001", "SN-002" }, loaded.Bindings[0].ProductCodes.ToArray());
        Assert.True(loaded.Bindings[0].LoadConfirmed);
        Assert.Equal("J12", loaded.Bindings[1].LocationCode);
        Assert.Equal(2, loaded.Bindings[0].ProductCount);
    }

    [Fact(DisplayName = "绑定簿_旧格式单值产品码一次性迁进清单")]
    public void Store_LegacySingleProductCode_MigratesToList()
    {
        // V0.0.97 之前盘上是一条记录一个 productCode；读取时迁进 ProductCodes
        // 清单（一次性搬家，此后盘上即新格式）。
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(SlotBindingStore.DefaultPath,
            """
            {"Bindings":[
              {"Rack":1,"Level":1,"productCode":"LEGACY-SN-1","BoundAt":"2026-11-07T08:00:00+08:00"}
            ]}
            """);
        var loaded = SlotBindingStore.Load()!;
        Assert.Single(loaded.Bindings);
        Assert.Equal(new[] { "LEGACY-SN-1" }, loaded.Bindings[0].ProductCodes.ToArray());
        Assert.Null(loaded.Bindings[0].LegacyProductCode);
    }

    [Fact(DisplayName = "绑定簿_缺文件回null并带原因")]
    public void Store_Missing_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        var reasons = new List<string>();
        Assert.Null(SlotBindingStore.Load(reasons));
        Assert.NotEmpty(reasons);
    }

    [Fact(DisplayName = "绑定簿_坏JSON回null并带原因")]
    public void Store_Corrupted_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(SlotBindingStore.DefaultPath, "{ 坏 JSON");
        var reasons = new List<string>();
        Assert.Null(SlotBindingStore.Load(reasons));
        Assert.Contains(reasons, r => r.Contains("损坏"));
    }

    [Fact(DisplayName = "绑定簿_脏记录读入时剔除")]
    public void Store_DirtyRecords_AreDropped()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(SlotBindingStore.DefaultPath,
            """
            {"Bindings":[
              {"Rack":1,"Level":1,"ProductCodes":["OK-1"],"BoundAt":"2026-11-07T08:00:00+08:00"},
              {"Rack":0,"Level":1,"ProductCodes":["BAD-RACK"],"BoundAt":"2026-11-07T08:00:00+08:00"},
              {"Rack":1,"Level":0,"ProductCodes":["BAD-LEVEL"],"BoundAt":"2026-11-07T08:00:00+08:00"}
            ]}
            """);
        var loaded = SlotBindingStore.Load()!;
        Assert.Single(loaded.Bindings);
        Assert.Equal("OK-1", loaded.Bindings[0].ProductCodes.Single());
    }

    // ---------------- 扫码枪配置 ----------------

    [Fact(DisplayName = "扫码配置_缺键自愈为缺省值")]
    public void ScannerOptions_Defaults_SelfHeal()
    {
        using var scope = new TestScope();
        var ini = scope.IniPath();
        var options = ScannerOptions.Load(ini);

        // V0.0.81 缺省=现场口径收口：启用也进缺省（部署目录重建/覆盖部署开箱即连）；
        Assert.True(options.Enabled);
        // TCP 预置三键照旧（Mode=Tcp + 枪侧 DTU IP + 8899 + 指令空）——
        // 缺键自愈直接得到现场口径，目标视为"已配置"。
        Assert.Equal("Tcp", options.NormMode);
        Assert.Equal(ScannerOptions.FieldTcpIp, options.TcpIp);
        Assert.Equal(8899, options.NormTcpPort);
        Assert.Equal("", options.TriggerCommand);
        Assert.True(options.HasPortTarget);
        Assert.Equal(9600, options.NormBaudRate);
        Assert.Equal(8, options.NormDataBits);
        Assert.Equal("1", options.NormStopBits);
        Assert.Equal("None", options.NormParity);
        Assert.True(File.Exists(ini));   // IniFile 读不到即回填（自愈语义）
    }

    [Fact(DisplayName = "扫码配置_存取往返")]
    public void ScannerOptions_SaveLoad_RoundTrip()
    {
        using var scope = new TestScope();
        var ini = scope.IniPath();
        new ScannerOptions
        {
            Enabled = true,
            PortName = "COM3",
            DeviceKeyword = "CH340",
            BaudRate = 115200,
            DataBits = 7,
            StopBits = "2",
            Parity = "Even",
            DebugLog = true,
        }.Save(ini);

        var loaded = ScannerOptions.Load(ini);
        Assert.True(loaded.Enabled);
        Assert.Equal("COM3", loaded.PortName);
        Assert.Equal("CH340", loaded.DeviceKeyword);
        Assert.Equal(115200, loaded.NormBaudRate);
        Assert.Equal(7, loaded.NormDataBits);
        Assert.Equal("2", loaded.NormStopBits);
        Assert.Equal("Even", loaded.NormParity);
        Assert.True(loaded.DebugLog);
        Assert.True(loaded.HasPortTarget);
    }

    [Fact(DisplayName = "扫码配置_非法值规范化回缺省")]
    public void ScannerOptions_InvalidValues_FallBack()
    {
        var options = new ScannerOptions
        {
            BaudRate = 999,
            DataBits = 99,
            StopBits = "9",
            Parity = "banana",
        };
        Assert.Equal(9600, options.NormBaudRate);
        Assert.Equal(8, options.NormDataBits);
        Assert.Equal("1", options.NormStopBits);
        Assert.Equal("None", options.NormParity);
    }

    // ---------------- 扫码枪服务（假实现，不碰串口） ----------------

    /// <summary>假扫码枪：事件由用例手动触发，验证宿主封装的事件转发与生命周期。</summary>
    private sealed class FakeScanner : IScanner
    {
        public event EventHandler<string>? SerialNumberScanned;
        public event EventHandler<bool>? ConnectionChanged;

        public bool IsOpen { get; private set; }
        public string Name => "FakeCOM";
        public bool Disposed { get; private set; }

        public bool Open()
        {
            IsOpen = true;
            ConnectionChanged?.Invoke(this, true);
            return true;
        }

        public bool SendTrigger() => true;
        public void Emit(string code) => SerialNumberScanned?.Invoke(this, code);

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            IsOpen = false;
            ConnectionChanged?.Invoke(this, false);
        }
    }

    [Fact(DisplayName = "扫码服务_转发条码与连接事件并干净释放")]
    public void ScannerService_ForwardsEvents_AndDisposes()
    {
        using var scope = new TestScope();
        var fake = new FakeScanner();
        ScanConfig? captured = null;
        var service = new ScannerBarcodeService(
            new ScannerOptions { Enabled = true, Mode = "Serial", PortName = "COM9", BaudRate = 115200 },
            cfg => { captured = cfg; return fake; });

        string? scanned = null;
        bool? connected = null;
        service.BarcodeScanned += (_, code) => scanned = code;
        service.ConnectionChanged += (_, state) => connected = state;

        Assert.True(service.Start());
        Assert.True(service.IsConnected);
        Assert.True(connected == true);
        Assert.Equal("Serial", captured!.Mode);       // 必须显式串口模式（缺省是 Tcp）
        Assert.Equal("COM9", captured.PortName);
        Assert.Equal(115200, captured.BaudRate);

        fake.Emit("SN-999");
        Assert.Equal("SN-999", scanned);

        service.Dispose();
        Assert.False(service.IsConnected);
        Assert.True(fake.Disposed);
        // 释放前已退订：假枪 Dispose 再发 ConnectionChanged(false) 不会被转发，
        // 订阅方最后收到的仍是"已连接"（状态由主窗退出链自行结束，无需假边沿）。
        Assert.Equal(true, connected);
    }

    [Fact(DisplayName = "扫码服务_未启用或未配置串口不启动")]
    public void ScannerService_NotConfigured_DoesNotStart()
    {
        using var scope = new TestScope();
        bool factoryCalled = false;

        using (var service = new ScannerBarcodeService(
                   new ScannerOptions { Enabled = false, PortName = "COM3" },
                   _ => { factoryCalled = true; return new FakeScanner(); }))
        {
            Assert.False(service.Start());
        }

        using (var service = new ScannerBarcodeService(
                   new ScannerOptions { Enabled = true, Mode = "Serial", PortName = "", DeviceKeyword = "" },
                   _ => { factoryCalled = true; return new FakeScanner(); }))
        {
            Assert.False(service.Start());
        }

        Assert.False(factoryCalled);
    }
}
