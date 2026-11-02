// =========================================================================
// ViewModel 测试：VM 编排逻辑（Fake 对话框 + TestScope 隔离落盘）。
//
// 覆盖：登录失败/跟随账号、主窗登录门禁与 gating、
//   修改密码 VM（规则/代改显隐）。后台节拍/真窗不在此测（UIA 冒烟覆盖）。
// 历史：模式窗 VM 用例已随单机链路删除（P2.1）。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Core.Mes;
using CureLine.Core.Modes;
using CureLine.Tests.Mocks;
using CimGateway.ViewModels;
using CureLine.ViewModels;

namespace CureLine.Tests;

public class ViewModelTests : IDisposable
{
    private readonly TestScope _scope = new();

    public ViewModelTests()
    {
        // 落盘目录先建（SQLite/kernel32 都不会自动建目录，与生产装配语义一致）。
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DatabaseFile)!);
        Directory.CreateDirectory(AppPaths.ConfigDir);
    }

    public void Dispose() => _scope.Dispose();

    private void SeedAccounts()
    {
        // 种到默认库（VM 经 UserAccountStore.Default 读同一路径；TestScope 已隔离）。
        var store = new UserAccountStore(new SqliteHelper(AppPaths.DatabaseFile));
        store.EnsureSeeded("test-admin-pwd", "test-dev-pwd");
    }

    [Fact(DisplayName = "登录VM失败给提示成功带角色")]
    public void LoginVm_FailTips_SuccessCarriesRole()
    {
        SeedAccounts();
        var vm = new LoginViewModel();
        vm.UserName = "admin";
        vm.Password = "wrong";
        vm.OkCommand.Execute(null);
        Assert.Equal(LanguageService.Tr("用户名或密码错误"), vm.Tip);
        Assert.Equal(LoginRole.None, vm.Role);

        vm.Password = "test-admin-pwd";
        bool? closed = null;
        vm.RequestClose += r => closed = r;
        vm.OkCommand.Execute(null);
        Assert.Equal(LoginRole.Admin, vm.Role);
        Assert.Equal("admin", vm.LoginName);
        Assert.Equal(true, closed);
    }

    [Fact(DisplayName = "登录VM用户名跟随账号密码")]
    public void LoginVm_FollowsAccountPassword()
    {
        SeedAccounts();
        string ini = AppPaths.ConfigIni;
        RunModeSettings.SaveLogin(ini, true, "dev", "test-dev-pwd");
        var vm = new LoginViewModel();
        Assert.Equal("dev", vm.UserName);
        Assert.True(vm.Remember);
        vm.UserName = "admin";
        Assert.False(vm.Remember);
        Assert.Equal("", vm.Password);
    }

    [Fact(DisplayName = "主窗VM未登录拦登录后放行")]
    public void MainVm_Gating_BlocksThenPasses()
    {
        SeedAccounts();
        var fake = new FakeDialogService
        {
            LoginResult = (true, LoginRole.Dev, "dev"),
            PlcResult = true,
        };
        using var vm = new MainWindowViewModel(fake);
        // 未登录点 PLC 设置：提示登录，不开配置窗
        vm.ShowGatewayCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
        Assert.DoesNotContain("GatewayConfig", fake.Calls);
        // 登录后：开配置窗；保存返回 true 走热重建连接（V0.0.41 起
        // RestartConnections 后台重建，程序化重启链路已删，不再有 Restart 调用）
        vm.LoginCommand.Execute(null);
        Assert.Equal($"dev ({LanguageService.Tr("切换/退出")})", vm.LoginHeader);
        Assert.True(vm.GatewayVisible);
        vm.ShowGatewayCommand.Execute(null);
        Assert.Contains("GatewayConfig", fake.Calls);
    }

    [Fact(DisplayName = "主窗VM退出登录清门禁")]
    public void MainVm_Logout_ClearsGating()
    {
        SeedAccounts();
        var fake = new FakeDialogService
        {
            LoginResult = (true, LoginRole.Admin, "admin"),
            ConfirmOrCancelResult = true,
        };
        using var vm = new MainWindowViewModel(fake);
        vm.LoginCommand.Execute(null);
        Assert.Contains("admin", vm.LoginHeader);
        Assert.False(vm.GatewayVisible);
        // 再点登录 → 确认退出（Yes）→ 清态
        vm.LoginCommand.Execute(null);
        Assert.Equal(LanguageService.Tr("登录"), vm.LoginHeader);
        Assert.False(vm.GatewayVisible);
    }

    [Fact(DisplayName = "改密码VM两次不一致拦成功提示")]
    public void ChangePwdVm_MismatchBlocks_SuccessNotifies()
    {
        SeedAccounts();
        var fake = new FakeDialogService();
        var vm = new ChangePasswordViewModel("admin", LoginRole.Admin, fake);
        Assert.False(vm.CanPickUser);
        Assert.True(vm.NeedOldPassword);
        vm.NewPassword = "a";
        vm.ConfirmPassword = "b";
        vm.OkCommand.Execute(null);
        Assert.Equal(LanguageService.Tr("两次新密码不一致"), vm.Tip);

        vm.OldPassword = "test-admin-pwd";
        vm.NewPassword = "new-pwd-1";
        vm.ConfirmPassword = "new-pwd-1";
        bool? closed = null;
        vm.RequestClose += r => closed = r;
        vm.OkCommand.Execute(null);
        Assert.Equal(true, closed);
        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
    }

    [Fact(DisplayName = "改密码VMdev代改藏旧密码行")]
    public void ChangePwdVm_DevProxy_HidesOldRow()
    {
        SeedAccounts();
        var fake = new FakeDialogService();
        var vm = new ChangePasswordViewModel("dev", LoginRole.Dev, fake);
        Assert.True(vm.CanPickUser);
        Assert.True(vm.NeedOldPassword);
        vm.SelectedUser = "admin";
        Assert.False(vm.NeedOldPassword);
    }

    // ---- 卡片业务化（V0.0.51 S2：6 接口微表计数 + 四态灯 + 未启用态）----

    [Fact(DisplayName = "卡片VM接口行计数与灯语义")]
    public void StationPanel_ApiStats_CountersAndLamp()
    {
        var vm = new StationPanelViewModel(0, _ => { }, _ => { });
        Assert.Equal(6, vm.ApiStats.Count);
        Assert.Equal(new[] { "0027", "0028", "0030", "0031", "0032", "0033" },
            vm.ApiStats.Select(s => s.Code).ToArray());
        Assert.All(vm.ApiStats, s => Assert.Equal("Idle", s.LampKind));
        Assert.Equal("Gray", vm.LampColor); // 初始未连

        vm.SetLive(true, false);            // 在线（Lime）
        Assert.Equal("Lime", vm.LampColor);

        vm.OnApiTriggered("API0030");
        Assert.Equal(1, vm.ApiStats[2].Triggered);
        Assert.Equal(1, vm.TotalTriggered);

        vm.OnApiDone(new ApiResult(0, "API0030", true, "1", "STATUS=1", 86));
        Assert.Equal(1, vm.ApiStats[2].Ok);
        Assert.Equal("Ok", vm.ApiStats[2].LampKind);
        Assert.Equal("1 / OK", vm.ApiStats[2].LastStatusText);
        Assert.Equal(0, vm.TotalFail);
        Assert.Equal("Lime", vm.LampColor); // 无失败仍在线色

        vm.OnApiDone(new ApiResult(0, "API0031", false, "0", "MES返回NG: API0031", 40));
        Assert.Equal(1, vm.ApiStats[3].Fail);
        Assert.Equal("Fail", vm.ApiStats[3].LampKind);
        Assert.Equal("2 / NG", vm.ApiStats[3].LastStatusText);
        Assert.Equal(1, vm.TotalFail);
        Assert.Equal("Yellow", vm.LampColor); // 今日有失败 → 黄
        Assert.Contains("触发 1", vm.HeadBadge);
        Assert.Contains("失败 1", vm.HeadBadge);

        vm.SetLive(false, true);            // 断连 → 红（优先级最高）
        Assert.Equal("Red", vm.LampColor);
    }

    [Fact(DisplayName = "卡片VM未启用态与最新消息三态")]
    public void StationPanel_DisabledAndLatestMessage()
    {
        var vm = new StationPanelViewModel(0, _ => { }, _ => { });
        // 未启用：配置跳过日志 → Enabled=false、灰灯、"未启用"徽标
        vm.AddLog(LanguageService.Tr("已配置为不连接本工站"));
        Assert.False(vm.Enabled);
        Assert.Equal("Gray", vm.LampColor);
        Assert.Equal(LanguageService.Tr("未启用"), vm.HeadBadge);
        Assert.Equal("Info", vm.LatestKind);

        // 启用台：连接成功 → Ok 消息；上传成功 → Ok；NG → Warn
        vm.AddLog(LanguageService.Tr("连接机台成功") + ",192.168.1.11");
        Assert.True(vm.Enabled);
        Assert.Equal("Ok", vm.LatestKind);
        vm.AddLog(LanguageService.Tr("MES上传成功") + ": API0030");
        Assert.Equal("Ok", vm.LatestKind);
        vm.AddLog(LanguageService.Tr("MES返回NG") + ": API0030");
        Assert.Equal("Warn", vm.LatestKind);
        Assert.Equal(4, vm.LogLines.Count); // 触发流同源累计（含未启用那条）
    }

    [Fact(DisplayName = "主窗VM汇总条初始态与单机视图切换")]
    public void MainVm_Summary_InitialStateAndSingleView()
    {
        var fake = new FakeDialogService();
        using var vm = new MainWindowViewModel(fake);
        Assert.True(vm.IsAllView);
        Assert.False(vm.IsSingleView);
        Assert.Equal(0, vm.TodayOk);
        Assert.Equal(0, vm.TodayFail);
        Assert.Equal("Idle", vm.MesLampKind);        // 从未触发=灰
        Assert.Equal(LanguageService.Tr("未触发"), vm.MesLastText);
        Assert.True(vm.MesEndpoint.Length > 0);       // 端点文本可显示（URL 空=占位"—"）
        Assert.Equal("200", vm.ScanIntervalMsText);   // 配置值缺省 200

        vm.SelectedViewIndex = 3;                     // 切单机视图
        Assert.False(vm.IsAllView);
        Assert.True(vm.IsSingleView);
        Assert.Equal(vm.ViewOptions[3], vm.SingleTitle);
        Assert.Equal(AppConfig.StationIps[2], vm.SingleIp);
        Assert.Equal("—", vm.SingleScanText);         // 尚无实测
    }

    // ---- 模拟触发按钮 + 内置模拟 MES（V0.0.52：mock 数据全链路串联） ----

    /// <summary>
    /// 模拟网关装配：Simulate=1 + 机台 IP/端口（模拟客户端对空 IP 拒连，必须给
    /// 非空值；模拟实现不真拨号，任意合法值即可）+ 6 接口触发地址与地址束
    /// （模拟寄存器布局，各接口区间错开互不覆盖，口径对齐 config.sample.ini）。
    /// URL 留空——由 VM 把内置桩补进去（这正是要测的接管规则）。
    /// </summary>
    private static void ConfigSimGateway()
    {
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        // URL 显式清空：桩接管规则吃"空/占位"两种形态——显式清空使本用例
        // 不受任何先序测试静态泄漏影响（曾有 MesLayerTests 脏 URL 泄漏事故）
        MesConfig.UrlApi0027 = ""; MesConfig.UrlApi0028 = "";
        MesConfig.UrlApi0030 = ""; MesConfig.UrlApi0031 = "";
        MesConfig.UrlApi0032 = ""; MesConfig.UrlApi0033 = "";
        string[] triggers = { "R13000", "R16000", "R17000", "R18000", "R19000", "R12000" };
        for (int i = 0; i < 8; i++)
        {
            AppConfig.StationIps[i] = $"192.168.1.{10 + i}";
            AppConfig.StationPorts[i] = 502;
        }
        for (int i = 0; i < 6; i++)
        {
            MesConfig.TriggerApi0027 = triggers[0]; MesConfig.TriggerApi0028 = triggers[1];
            MesConfig.TriggerApi0030 = triggers[2]; MesConfig.TriggerApi0031 = triggers[3];
            MesConfig.TriggerApi0032 = triggers[4]; MesConfig.TriggerApi0033 = triggers[5];
            int data = 13100 + i * 1000;   // 各接口数据区错开 1000
            var set = MesConfig.ApiAddresses[i];
            set.StationData = $"R{data}";
            set.MesStatus = $"R{data + 10}";
            set.MesMsg = $"R{data + 20}";
            set.MesData = $"R{data + 30}";
            set.MesComplete = $"R{data + 40}";
        }
    }

    /// <summary>轮询等待条件成立（模拟链路跨线程：连接/扫描/POST 都在后台）。
    /// dump=超时时的状态转储（链路飘移排障直接看断在哪一环）。</summary>
    private static async Task WaitUntilAsync(Func<bool> cond, Func<string>? dump = null,
        int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond())
                return;
            await Task.Delay(25);
        }
        string detail = dump != null ? " | " + dump() : "";
        Assert.True(cond(), "等待超时：模拟链路未在时限内消化" + detail);
    }

    [Fact(DisplayName = "主窗VM模拟模式Mock按钮全链路成功链")]
    public async Task MainVm_Simulate_MockTrigger_FullChain_Ok()
    {
        ConfigSimGateway();
        var fake = new FakeDialogService();
        using var vm = new MainWindowViewModel(fake);
        Assert.True(vm.IsSimulateMode);
        // 开机自动连接（模拟建连即时）：等 8 台真在线（心跳灯已翻转——初始灰灯
        // 也计入 OnlineCount，等它没有意义，必须等 Lime/Green）
        await WaitUntilAsync(() => vm.Stations.All(
            s => s.LampColor is "Lime" or "Green"));
        // 6 击覆盖 6 接口（tick0→台0/0027，tick1→台1/0028，……）
        for (int i = 0; i < 6; i++)
            vm.MockTriggerCommand.Execute(null);
        // 等全部触发消化完（有结果=Ok 或 Fail 都算——消化齐了才有资格断言零失败）
        string Dump()
        {
            string errs = string.Join(" / ", vm.Stations
                .Where(s => s.ApiStats.Any(r => r.Fail > 0))
                .SelectMany(s => MesLogger.GetCachedLogs(s.StationId)
                    .Where(l => l.Contains("Http Request") || l.Contains("Http Error")))
                .TakeLast(6));
            return $"Ok={vm.TodayOk} Fail={vm.TodayFail} Trig={vm.TodayTriggered} " +
                $"Stub={MockMesServer.BaseUrl ?? "null"} | " +
                string.Join("; ", vm.Stations.SelectMany(s =>
                    s.ApiStats.Where(r => r.Fail > 0)
                        .Select(r => $"台{s.StationId + 1}/{r.Code}:{r.LastMessage}"))) +
                " | LOGS: " + errs;
        }
        await WaitUntilAsync(
            () => vm.TodayTriggered >= 6 && vm.TodayOk + vm.TodayFail >= vm.TodayTriggered,
            Dump);
        Assert.True(vm.TodayTriggered >= 6, Dump());
        Assert.True(vm.TodayFail == 0, Dump());   // 混进失败≠接受——用转储抓根因
        Assert.Equal("Ok", vm.MesLampKind);
        // 各台一击（自动演示 7s 一发可能插进来，用 >= 防竞态）
        for (int s = 0; s < 6; s++)
            Assert.True(vm.Stations[s].TotalTriggered >= 1, $"机台{s + 1} 未收到注入");
        var row = vm.Stations[0].ApiStats.First(x => x.Code == "0027");
        Assert.True(row.Triggered >= 1);
        Assert.True(row.Ok >= 1);
        Assert.Equal("Ok", row.LampKind);
        Assert.Equal("1 / OK", row.LastStatusText);
    }

    [Fact(DisplayName = "主窗VM模拟模式NG链路拒绝回写报警窗")]
    public async Task MainVm_Simulate_MockTrigger_NgChain()
    {
        ConfigSimGateway();
        // 自配桩 MES（拒绝应答）挂到 API0028——非占位 URL，内置桩不得接管（透传规则）
        using var stub = new MesStubServer();
        stub.EnqueueReject();
        MesConfig.UrlApi0028 = stub.Url;
        var fake = new FakeDialogService();
        using var vm = new MainWindowViewModel(fake);
        await WaitUntilAsync(() => vm.Stations.All(
            s => s.LampColor is "Lime" or "Green"));
        // tick0=台0/0027（内置桩→成功），tick1=台1/0028（自配桩→拒绝）
        vm.MockTriggerCommand.Execute(null);
        vm.MockTriggerCommand.Execute(null);
        await WaitUntilAsync(() => vm.TodayOk >= 1 && vm.TodayFail >= 1);
        var row28 = vm.Stations[1].ApiStats.First(x => x.Code == "0028");
        Assert.Equal("Fail", row28.LampKind);
        Assert.Equal("2 / NG", row28.LastStatusText);
        Assert.Equal("Yellow", vm.Stations[1].LampColor);  // 在线 + 今日有失败
        Assert.Contains(fake.Calls, c => c.StartsWith("Alarm:"));  // NG → 报警窗链路
    }
}
