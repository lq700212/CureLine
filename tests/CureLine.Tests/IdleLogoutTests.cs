// =========================================================================
// IdleLogoutTests：登录空闲自动退出（V0.0.93）
//   ① Core 监视器纯逻辑（假时钟推进）：超时判定/活动重置/防连发/禁用/现取；
//   ② 主窗 VM 接线：ctor 注入监视器（假时钟），登录→推进→手动 Tick→
//      自动退回未登录态（OperatorText 复位+门禁收回）。
// 测试态 DispatcherTimer 不走消息泵，Tick 全部手动调——时刻完全可控。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Tests.Mocks;
using Xunit;

namespace CureLine.Tests;

public class IdleLogoutTests
{
    /// <summary>临时目录下补建 config/（WritePrivateProfileString 不自动建目录）。</summary>
    private static void EnsureConfigDir() =>
        Directory.CreateDirectory(AppPaths.ConfigDir);

    // ---------- Core 监视器（假时钟） ----------

    /// <summary>建监视器：假时钟 T0=2026-11-10 08:00 起步；返回（监视器, 推进分钟, 触发记录）。</summary>
    private static (IdleLogoutMonitor Monitor, Action<int> Push, List<int> Fired) Create(int minutes = 15)
    {
        var now = new DateTime(2026, 11, 10, 8, 0, 0);
        var fired = new List<int>();
        var monitor = new IdleLogoutMonitor(() => minutes, () => now);
        monitor.LogoutDue += fired.Add;
        return (monitor, min => now = now.AddMinutes(min), fired);
    }

    [Fact]
    public void Monitor_TimeoutAtExactLimit_FiresOnce()
    {
        var (monitor, push, fired) = Create();
        push(14);
        monitor.Tick();
        Assert.Empty(fired);                 // 14 分未满不触发
        push(1);
        monitor.Tick();
        Assert.Equal(new[] { 15 }, fired);   // 恰满 15 分触发，参数=生效上限
        monitor.Tick();                      // 基线已重置：不连发
        Assert.Single(fired);
    }

    [Fact]
    public void Monitor_Activity_ResetsBaseline()
    {
        var (monitor, push, fired) = Create();
        push(10);
        monitor.NotifyActivity();            // 活动：基线推到当下
        push(10);
        monitor.Tick();
        Assert.Empty(fired);                 // 距活动仅 10 分
        push(5);
        monitor.Tick();
        Assert.Single(fired);                // 距活动满 15 分才触发
    }

    [Fact]
    public void Monitor_DisabledZeroMinutes_NeverFires()
    {
        var (monitor, push, fired) = Create(minutes: 0);
        push(120);
        monitor.Tick();
        Assert.Empty(fired);                 // 0=不启用，晾多久都不触发
    }

    [Fact]
    public void Monitor_ProviderChange_TakesEffectNextTick()
    {
        var now = new DateTime(2026, 11, 10, 8, 0, 0);
        var fired = new List<int>();
        int minutes = 0;                     // 先禁用
        var monitor = new IdleLogoutMonitor(() => minutes, () => now);
        monitor.LogoutDue += fired.Add;

        now = now.AddHours(1);
        monitor.Tick();
        Assert.Empty(fired);

        minutes = 5;                         // 改上限：禁用期基线跟随当下，从零计
        now = now.AddMinutes(4);
        monitor.Tick();
        Assert.Empty(fired);
        now = now.AddMinutes(1);
        monitor.Tick();
        Assert.Equal(new[] { 5 }, fired);    // 现取生效：新上限下一拍起算
    }

    // ---------- 主窗 VM 接线 ----------

    [Fact]
    public void MainVm_IdleLogout_AutoSignsOut()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();

        var now = new DateTime(2026, 11, 10, 8, 0, 0);
        var monitor = new IdleLogoutMonitor(() => 15, () => now);
        using var vm = new CureStation.ViewModels.MainViewModel(fake, monitor);

        fake.LoginResult = ("operator", OperatorRole.Operator);
        vm.LoginCommand.Execute(null);
        Assert.Equal("操作员：operator", vm.OperatorText);

        now = now.AddMinutes(15);            // 登录后 15 分钟无操作
        vm.IdleMonitor.Tick();
        Assert.Equal("未登录", vm.OperatorText);
        Assert.False(vm.IsLoggedIn);
        Assert.False(vm.IsSettingsEnabled);  // 门禁收回：未登录=设置整钮禁用+悬停提示（V0.0.95 用户拍板）
    }

    [Fact]
    public void MainVm_IdleLogout_ActivityKeepsSession()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();

        var now = new DateTime(2026, 11, 10, 8, 0, 0);
        var monitor = new IdleLogoutMonitor(() => 15, () => now);
        using var vm = new CureStation.ViewModels.MainViewModel(fake, monitor);

        fake.LoginResult = ("operator", OperatorRole.Operator);
        vm.LoginCommand.Execute(null);

        now = now.AddMinutes(10);
        vm.IdleMonitor.NotifyActivity();     // 键鼠活动：基线重置
        now = now.AddMinutes(10);
        vm.IdleMonitor.Tick();
        Assert.True(vm.IsLoggedIn);          // 距活动仅 10 分——会话保持

        now = now.AddMinutes(5);
        vm.IdleMonitor.Tick();
        Assert.False(vm.IsLoggedIn);         // 距活动满 15 分——自动退出
    }

    [Fact]
    public void MainVm_IdleLogout_NotLoggedIn_IsNoop()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();

        var now = new DateTime(2026, 11, 10, 8, 0, 0);
        var monitor = new IdleLogoutMonitor(() => 15, () => now);
        using var vm = new CureStation.ViewModels.MainViewModel(fake, monitor);

        now = now.AddHours(2);               // 未登录晾 2 小时
        vm.IdleMonitor.Tick();
        Assert.Equal("未登录", vm.OperatorText);   // 无语义不误伤
        Assert.False(vm.IsLoggedIn);
    }
}
