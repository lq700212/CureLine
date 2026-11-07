// =========================================================================
// LogWindowViewModel 测试：日志窗打开时预填进程内历史
//
// 锁定的口径（2026-11-07 用户实测"日志窗一开是空的"）：
//   构造即从 LocalLogService 进程内缓存预填，最新在上、封顶 200 条——
//   只订阅 Written 会让打开前已发生的日志（启动/连接/报警）全部丢失。
// =========================================================================

using CureLine.Core.Services;
using CureLine.Tests.Mocks;
using CureStation.ViewModels;

namespace CureLine.Tests;

public class LogWindowViewModelTests
{
    [Fact(DisplayName = "日志窗打开时预填历史日志（最新在上）")]
    public void Ctor_PrefillsRecentHistory_NewestOnTop()
    {
        using var scope = new TestScope();
        LocalLogService.ClearRecentForTests();
        LocalLogService.Write("历史甲", -1);
        LocalLogService.Write("历史乙", -1);

        var vm = new LogWindowViewModel(
            new LogSystemSnapshot("12:00:00", Array.Empty<SystemStatusRow>()));
        try
        {
            Assert.Contains(vm.LogLines, l => l.EndsWith("历史甲"));
            Assert.Contains(vm.LogLines, l => l.EndsWith("历史乙"));
            int a = vm.LogLines.ToList().FindIndex(l => l.EndsWith("历史甲"));
            int b = vm.LogLines.ToList().FindIndex(l => l.EndsWith("历史乙"));
            Assert.True(b < a);   // 最新（后写）在上
        }
        finally
        {
            vm.Detach();
        }
    }
}
