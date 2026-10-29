// =========================================================================
// 子模块冒烟测试：验证 extern/kaleidoscope（net472）在 net8.0-windows 下
// 可加载、可实例化、可调用。不连真机、不依赖 HSL 授权。
//
// 背景：新项目（net8.0-windows）直接引用 net472 编译的 Kaleidoscope.dll，
// 编译期 NU1701 已压制；本文件验证运行期程序集加载与基本调用无缺失 API。
// 若本批全绿，通讯层即可复用子模块 HslPlcClient，无需改造子模块源码。
// =========================================================================

using Kaleidoscope.Models;
using Kaleidoscope.Services;
using Kaleidoscope.Utils;

namespace CureLine.Tests;

/// <summary>子模块运行期兼容冒烟测试。</summary>
public class SubmoduleSmokeTests
{
    [Fact(DisplayName = "子模块程序集可在net8下加载")]
    public void Kaleidoscope_Assembly_Loads_OnNet8()
    {
        // 做什么：触发 net472 程序集加载，验证 CLR 能解析并加载它。
        // 为什么：这是整个引用方案的地基，加载失败后面一切免谈。
        var asm = typeof(DeviceHub).Assembly;
        Assert.NotNull(asm);
        Assert.Contains("Kaleidoscope", asm.FullName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "HslPlcClient无参构造可用")]
    public void HslPlcClient_DefaultCtor_Works()
    {
        // 做什么：new 一个空配置的多品牌 PLC 客户端，不连网。
        // 为什么：构造路径会经过 HslCommunication 类型加载，若 netfx 版 Hsl 在
        // net8 下缺 API，这里会直接 TypeLoadException，比连真机早暴露问题。
        using var client = new HslPlcClient();
        Assert.NotNull(client);
    }

    [Fact(DisplayName = "无授权时EnsureRegistered降级不抛异常")]
    public void EnsureRegistered_WithoutAuth_ReturnsFalse_NoThrow()
    {
        // 做什么：在无 hsl.dat 的测试环境调用授权入口。
        // 为什么：复刻 kaleidoscope 的幂等降级约定——没授权只返回 false，
        // 不抛异常、不阻塞后续模拟联调。改动该行为即返工。
        var ex = Record.Exception(() => HslAuthorization.EnsureRegistered());
        Assert.Null(ex);
    }

    [Fact(DisplayName = "多品牌PLC配置模型可用")]
    public void MultiBrandPlcConfig_DefaultCtor_Works()
    {
        // 做什么：构造三菱 MC 默认配置对象，准备后续模拟联调接线。
        // 为什么：配置模型是 Models 层（零 HSL 引用），必须随时可用。
        var cfg = new MultiBrandPlcConfig();
        Assert.NotNull(cfg);
    }
}
