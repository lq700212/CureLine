// =========================================================================
// CuringMesPosterTests：单线过账服务（P3.2 后半）
// 端到端：Poster（后台 Task）→ MesHttpClient → 本地桩 MES（MesStubServer），
// 断言报文字段与累计口径；配置分支（开关/URL/时机/工位/单位）逐项锁定。
// =========================================================================

using System.Diagnostics;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class CuringMesPosterTests
{
    /// <summary>标准测试工单（R 途程 R-RC1 / L 途程 L-RC1）。</summary>
    private static WorkOrderBook MakeBook(bool uploadEnabled = true) => new()
    {
        Right = new WorkOrderSlot { RcNo = "R-RC1", PresetQty = 100, ProducedQty = 5 },
        Left = new WorkOrderSlot { RcNo = "L-RC1", PresetQty = 50, ProducedQty = 2 },
        EmpNo = "EMP001",
        BcNo = "BC-BATCH-01",
        PtNo1 = "PT-A",
        PtNo2 = "PT-B",
        UploadEnabled = uploadEnabled,
    };

    private (CuringMesPoster Poster, List<string> Logs, WorkOrderBook Book) Make(
        CuringMesOptions? options = null, WorkOrderBook? book = null)
    {
        var opts = options ?? new CuringMesOptions { Enabled = true };
        var captured = book ?? MakeBook();
        var logs = new List<string>();
        // 过账在后台 Task 并发跑——log 回调加锁（List 非线程安全）
        var poster = new CuringMesPoster(opts, () => captured,
            log: m => { lock (logs) logs.Add(m); });
        return (poster, logs, captured);
    }

    private static void WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond())
                return;
            Thread.Sleep(50);
        }
        throw new Xunit.Sdk.XunitException("等待条件超时（过账后台任务未完成）");
    }

    // ---------- 进站 ----------

    [Fact]
    public void BoardIn_Posts_Api0030_ActionI_WithWorkOrderAndStaticFields()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0030 = mes.Url;
        MesParamStore.PLANT = "PLANT-MEM"; // 记忆值回退源
        var (poster, _, _) = Make(new CuringMesOptions
        {
            Enabled = true,
            MacNo = "MAC-CFG",       // 配置优先
            CheckResult = "",        // 空 → 回退记忆 "0"
        });

        poster.OnTrayArrived(1, 1);
        mes.WaitForRequests(1);

        // 接口身份以报文特征断言（桩 MES 单监听地址，URL 恒为基地址）
        Assert.Contains("\"ACTION\":\"I\"", mes.Bodies[0]);
        Assert.Contains("\"RC_NO\":\"R-RC1\"", mes.Bodies[0]);
        Assert.Contains("\"BC_NO\":\"BC-BATCH-01\"", mes.Bodies[0]);
        Assert.Contains("\"PT_NO\":\"PT-A\"", mes.Bodies[0]);
        Assert.Contains("\"MAC_NO\":\"MAC-CFG\"", mes.Bodies[0]);
        Assert.Contains("\"CHECK_RESULT\":\"0\"", mes.Bodies[0]);
        Assert.Contains("\"PLANT\":\"PLANT-MEM\"", mes.Bodies[0]);
    }

    [Fact]
    public void PostInAtStart_ArriveSilent_CuringStartPosts()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions { Enabled = true, InAt = PostInAt.Start });

        poster.OnTrayArrived(1, 1);   // 到位：Start 口径不发
        Thread.Sleep(200);            // 给可能的误发留时间（下一断言兜底）
        Assert.Empty(mes.Bodies);

        poster.OnCuringStarted(2, 2); // 开计时：发
        mes.WaitForRequests(1);
        Assert.Contains("\"ACTION\":\"I\"", mes.Bodies[0]);
    }

    [Fact]
    public void EmptyRcNo_SkipsPosting()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0030 = mes.Url;
        var book = MakeBook();
        book.Right.RcNo = "";
        var (poster, logs, _) = Make(book: book);

        poster.OnTrayArrived(1, 1);
        WaitUntil(() => logs.Any(m => m.Contains("工单途程单号为空")));

        Assert.Empty(mes.Bodies);
    }

    // ---------- 双架工位映射（V0.0.20 RackSides） ----------

    [Theory]
    [InlineData("", 1, "R")]            // 空=全部用 Side（单架零感知）
    [InlineData("11-20:L", 11, "L")]    // 第二台架首站命中段
    [InlineData("11-20:L", 20, "L")]    // 第二台架末站命中段
    [InlineData("11-20:L", 10, "R")]    // 未命中段回落 Side
    [InlineData("11-20:l", 12, "L")]    // 工位小写规范化
    [InlineData("5:R;11-20:L", 5, "R")] // 单号段 + 多段
    [InlineData("5:L", 5, "L")]         // 单号段
    [InlineData("bad", 1, "R")]         // 非法段忽略 → 回落 Side
    [InlineData("0-9:L", 1, "R")]       // 架号 0 非法忽略
    [InlineData("abc-20:L", 1, "R")]    // 段起始非数字忽略
    public void SideOfRack_MapsByConfiguredRanges_FallsBackToSide(
        string rackSides, int rack, string expected)
    {
        // 双工位开=老双架映射口径（开关关恒回 Side，另有单工位专例锁定）
        var o = new CuringMesOptions { DualSide = true, Side = "R", RackSides = rackSides };
        Assert.Equal(expected, o.SideOfRack(rack));
    }

    [Fact]
    public void BoardOut_Rack11MappedToL_PostsLeftSlotAndLStaticParams()
    {
        // 双架端到端：架 11（2号架）按 RackSides 映射到 L 工位——
        // 工位槽取 book.Left（RC_NO=L-RC1），机台号用 L 工位专用值
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        mes.EnqueueOk(); // 0027
        mes.EnqueueOk(); // 0030
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions
        {
            Enabled = true,
            DualSide = true, // 双架口径用例显式开开关（缺省单工位会忽略映射）
            Side = "R",
            RackSides = "11-20:L",
            MacNo = "MAC-R",
            MacNo_L = "MAC-L2",
        });

        poster.OnTrayLeft(11, 2, SlotPhase.Done, 600);
        mes.WaitForRequests(2);

        Assert.Contains("\"RC_NO\":\"L-RC1\"", mes.Bodies[0]);
        Assert.Contains("\"RC_NO\":\"L-RC1\"", mes.Bodies[1]);
        Assert.Contains("\"MAC_NO\":\"MAC-L2\"", mes.Bodies[0]);
        Assert.Contains("\"MAC_NO\":\"MAC-L2\"", mes.Bodies[1]);
    }

    [Fact]
    public void BoardOut_Rack1Unmapped_KeepsSideR()
    {
        // 对照：同配置下 1 号架未命中映射段 → 照旧 R 工位 + 主机台号
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        mes.EnqueueOk();
        mes.EnqueueOk();
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions
        {
            Enabled = true,
            Side = "R",
            RackSides = "11-20:L",
            MacNo = "MAC-R",
            MacNo_L = "MAC-L2",
        });

        poster.OnTrayLeft(1, 1, SlotPhase.Done, 300);
        mes.WaitForRequests(2);

        Assert.Contains("\"RC_NO\":\"R-RC1\"", mes.Bodies[0]);
        Assert.Contains("\"MAC_NO\":\"MAC-R\"", mes.Bodies[0]);
    }

    [Fact]
    public void Options_RackSides_SaveLoadRoundTrip()
    {
        // 新键 ini 往返（铁律 8：新增配置键必须 Load/Save 同步 + 缺省值自愈）
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var o = new CuringMesOptions
        {
            RackSides = "11-20:L",
            MacNo = "MAC-R",
            MacNo_L = "MAC-L2",
            MacLocNo = "LOC-R",
            MacLocNo_L = "LOC-L2",
        };
        o.Save();
        var loaded = CuringMesOptions.Load();
        Assert.Equal("11-20:L", loaded.RackSides);
        Assert.Equal("MAC-R", loaded.MacNo);
        Assert.Equal("MAC-L2", loaded.MacNo_L);
        Assert.Equal("LOC-R", loaded.MacLocNo);
        Assert.Equal("LOC-L2", loaded.MacLocNo_L);
        // 缺省回落：L 工位未配专用值时用主值（MacNoOf/MacLocNoOf）
        var plain = new CuringMesOptions { MacNo = "MAIN", MacNo_L = "" };
        Assert.Equal("MAIN", plain.MacNoOf("L"));
        Assert.Equal("MAIN", plain.MacNoOf("R"));
    }

    [Fact]
    public void Options_DualSide_SaveLoadRoundTrip()
    {
        // 双工位开关 ini 往返（铁律 8）；缺省=单工位（现场无双工位→缺省关）
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var o = new CuringMesOptions { DualSide = true, Side = "L" };
        o.Save();
        Assert.True(CuringMesOptions.Load().DualSide);

        o.DualSide = false;
        o.Save();
        Assert.False(CuringMesOptions.Load().DualSide);

        Assert.False(new CuringMesOptions().DualSide); // 代码缺省同样单工位
    }

    [Fact]
    public void SideOfRack_SingleMode_IgnoresRackSides()
    {
        // 单工位（双工位开关关）：RackSides 即使残留配置也忽略——恒回 Side
        // （防现场误配残留的 "11-20:L" 把唯一一套工单引到空 L 槽）
        var single = new CuringMesOptions { DualSide = false, Side = "R", RackSides = "1-99:L" };
        Assert.Equal("R", single.SideOfRack(15));

        // 双工位开：同配置命中映射段 → L（回归老口径）
        var dual = new CuringMesOptions { DualSide = true, Side = "R", RackSides = "1-99:L" };
        Assert.Equal("L", dual.SideOfRack(15));
        Assert.Equal("R", dual.SideOfRack(100)); // 未命中段回落 Side
    }

    [Fact]
    public void BoardOut_SingleSide_MapsAllRacksToSide()
    {
        // 单工位出站端到端：架 15（残留映射段 11-20:L 内）也按 Side=R 报
        // R 工位途程 + 主机台号（L 专用参数不上）
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        mes.EnqueueOk();
        mes.EnqueueOk();
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions
        {
            Enabled = true,
            Side = "R",
            RackSides = "11-20:L", // 残留双架配置——单工位（缺省 DualSide=false）下忽略
            MacNo = "MAC-R",
            MacNo_L = "MAC-L2",
        });

        poster.OnTrayLeft(15, 1, SlotPhase.Done, 300);
        mes.WaitForRequests(2);

        Assert.Contains("\"RC_NO\":\"R-RC1\"", mes.Bodies[0]);
        Assert.Contains("\"MAC_NO\":\"MAC-R\"", mes.Bodies[0]);
    }

    // ---------- 出站链 ----------

    [Fact]
    public void BoardOut_Posts_Api0027_Seconds_Then_Api0030_CountsProduced()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        mes.EnqueueOk(); // 0027
        mes.EnqueueOk(); // 0030
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        MesParamStore.PRA_CODE = "PRA-MEM";
        var (poster, _, book) = Make(new CuringMesOptions
        {
            Enabled = true,
            TimeUnit = CuringTimeUnit.Seconds,
            PraCode = "", // 空 → 回退记忆
        });

        poster.OnTrayLeft(1, 1, SlotPhase.Done, curedSeconds: 7200); // 120 分钟
        mes.WaitForRequests(2);
        WaitUntil(() => book.Right.ProducedQty == 6); // 出站被接受 → R 工位 +1

        // 第一发：API0027 固化时间——外层数组 + PRA_Value（顺序即老项目口径：
        // Bodies[0] 必是 0027 报文，Bodies[1] 才是出站；桩单地址，接口身份看报文）
        Assert.StartsWith("[", mes.Bodies[0].TrimStart());
        Assert.Contains("\"PRA_Value\":\"7200\"", mes.Bodies[0]);
        Assert.Contains("\"PRA_CODE\":\"PRA-MEM\"", mes.Bodies[0]);
        Assert.Contains("\"RC_NO\":\"R-RC1\"", mes.Bodies[0]);
        // 第二发：API0030 出站 ACTION=O
        Assert.Contains("\"ACTION\":\"O\"", mes.Bodies[1]);
        // 落盘验证
        var reloaded = WorkOrderStore.Load()!;
        Assert.Equal(6, reloaded.Right.ProducedQty);
    }

    [Fact]
    public void BoardOut_TimeUnitMinutes_ConvertsSecondsToMinutes()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions
        {
            Enabled = true,
            TimeUnit = CuringTimeUnit.Minutes,
        });

        poster.OnTrayLeft(1, 1, SlotPhase.Done, curedSeconds: 7200);
        mes.WaitForRequests(2);

        Assert.Contains("\"PRA_Value\":\"120\"", mes.Bodies[0]); // 7200 秒 → 120 分钟
    }

    [Fact]
    public void BoardOut_MesReject_DoesNotCount()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        mes.EnqueueOk();     // 0027 成功
        mes.EnqueueReject(); // 0030 被拒
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, book) = Make();

        poster.OnTrayLeft(1, 1, SlotPhase.Done, 7200);
        mes.WaitForRequests(2);
        Thread.Sleep(200); // 给（不会发生的）累计留时间

        Assert.Equal(5, book.Right.ProducedQty); // 出站被拒 → 不累计
    }

    [Fact]
    public void WaitingGateLeave_ArriveMode_PostsOutWithZeroSeconds()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions { Enabled = true, InAt = PostInAt.Arrive });

        poster.OnTrayLeft(1, 1, SlotPhase.WaitingGate, curedSeconds: 0);
        mes.WaitForRequests(2);

        Assert.Contains("\"PRA_Value\":\"0\"", mes.Bodies[0]); // 没开计时 → 固化 0 秒
        Assert.Contains("\"ACTION\":\"O\"", mes.Bodies[1]);    // 有进必有出
    }

    [Fact]
    public void AbortLeave_NoPost_WhenPostAbortOutOff()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, logs, _) = Make(new CuringMesOptions
        {
            Enabled = true,
            PostAbortOut = false,
        });

        poster.OnTrayLeft(1, 1, SlotPhase.Curing, 3600); // 计时中取件
        WaitUntil(() => logs.Any(m => m.Contains("按配置不上报出站")));

        Assert.Empty(mes.Bodies);
    }

    [Fact]
    public void SideL_UsesLeftSlotAndCountsLeft()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        mes.EnqueueOk();
        mes.EnqueueOk();
        MesConfig.UrlApi0027 = mes.Url;
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, book) = Make(new CuringMesOptions { Enabled = true, Side = "L" });

        poster.OnTrayLeft(3, 2, SlotPhase.Done, 600);
        mes.WaitForRequests(2);
        WaitUntil(() => book.Left.ProducedQty == 3);

        Assert.Contains("\"RC_NO\":\"L-RC1\"", mes.Bodies[0]);
        Assert.Equal(3, book.Left.ProducedQty);
        Assert.Equal(5, book.Right.ProducedQty); // R 不动
    }

    // ---------- 发送门槛 ----------

    [Fact]
    public void Disabled_SendsNothing()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0030 = mes.Url;
        MesConfig.UrlApi0027 = mes.Url;
        var (poster, logs, _) = Make(new CuringMesOptions { Enabled = false });

        poster.OnTrayArrived(1, 1);
        poster.OnTrayLeft(1, 1, SlotPhase.Done, 7200);
        // 进站 1 条 + 出站链 0027 门槛 1 条（|| 短路，0030 不再查）= 2 条
        WaitUntil(() => { lock (logs) return logs.Count(m => m.Contains("总开关未启用")) >= 2; });

        Assert.Empty(mes.Bodies);
    }

    [Fact]
    public void EmptyUrl_SendsNothing()
    {
        using var scope = new TestScope();
        MesConfig.UrlApi0030 = ""; // 未配置
        var (poster, logs, _) = Make(new CuringMesOptions { Enabled = true });

        poster.OnTrayArrived(1, 1);
        WaitUntil(() => logs.Any(m => m.Contains("接口 URL 未配置")));

        // 无桩可断言 URL——只要没抛异常且日志正确即算门槛生效
        Assert.True(true);
    }

    // ---------- 物料上传（工单窗手动） ----------

    [Fact]
    public void UploadMaterial_Posts_Api0032_WithWorkOrderFields()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0032 = mes.Url;
        MesParamStore.PLANT = "PLANT-MEM";
        var (poster, _, _) = Make(new CuringMesOptions { Enabled = true, MacLocNo = "LOC-CFG" });

        poster.UploadMaterial("L");
        mes.WaitForRequests(1);

        // 接口身份以报文特征断言（EMP_NO 仅 API0032 携带）
        Assert.Contains("\"RC_NO\":\"L-RC1\"", mes.Bodies[0]); // L 按钮 → L 工位途程
        Assert.Contains("\"BC_NO\":\"BC-BATCH-01\"", mes.Bodies[0]);
        Assert.Contains("\"EMP_NO\":\"EMP001\"", mes.Bodies[0]);
        Assert.Contains("\"PT_NO\":\"PT-A\"", mes.Bodies[0]);
        Assert.Contains("\"MAC_LOC_NO\":\"LOC-CFG\"", mes.Bodies[0]);
        Assert.Contains("\"PLANT\":\"PLANT-MEM\"", mes.Bodies[0]);
    }

    [Fact]
    public void UploadMaterial_EnableOff_SendsNothing()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0032 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions { Enabled = true },
            book: MakeBook(uploadEnabled: false));

        poster.UploadMaterial("R");
        Thread.Sleep(200); // 给可能的误发留时间（下一断言兜底）

        Assert.Empty(mes.Bodies); // 使能关双保险拦下
    }

    // ---------- 顶栏状态事件 ----------

    [Fact]
    public void PostingChanged_FiresWithResultText()
    {
        using var scope = new TestScope();
        using var mes = new MesStubServer();
        MesConfig.UrlApi0030 = mes.Url;
        var (poster, _, _) = Make(new CuringMesOptions { Enabled = true });
        var texts = new List<string>();
        poster.PostingChanged += texts.Add;

        poster.OnTrayArrived(1, 1);
        mes.WaitForRequests(1);
        WaitUntil(() => texts.Count > 0);

        Assert.Contains(texts, t => t.Contains("进站") && t.Contains("OK"));
    }
}
