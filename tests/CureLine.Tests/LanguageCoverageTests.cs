// =========================================================================
// 中英词典覆盖测试：UI 实际 Tr() 的键必须在英文表里找得到
//
// 背景：LanguageService 是中英两套词典合并的（单机 50+ / 多机 60+），
// 漏一个键，切英文后界面就蹦一句中文。查不到回退原文是 heritage 语义，
// 但"常用键全覆盖"是本文件要锁的：StatusKey 4 状态 + 物料状态 +
// 8 机台名 + 设置登录链 + 网关流程键，切 EN 后必须全变英文。
// 新增界面文案时，顺手来这里加一个键。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class LanguageCoverageTests : IDisposable
{
    private readonly TestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact(DisplayName = "UI常用键英文全覆盖")]
    public void UiKeys_AllTranslated()
    {
        LanguageService.CurrentLanguage = "EN";
        try
        {
            string[] keys =
            {
                "物料装载", "物料卸载", "强制出料", "资料获取", "物料状态",
                "连接机台成功", "连接设备", "CIM通信网关", "是否退出?",
                "设置", "登录", "用户名", "密码", "确定", "取消", "保存",
                "运行模式", "单机固化收料", "多机通用网关",
                "创建桌面快捷方式", "桌面快捷方式已创建", "桌面快捷方式已存在",
                // 菜单栏：运行模式独立按钮 + 设置下拉 + 登录按钮（双账号门禁）
                "网关设置", "请先登录", "请登录", "切换/退出",
                "当前已登录", "是否退出登录？（点否则切换账号）",
                "需要 dev 权限",
                // 修改密码窗（设置下拉，登录后可进）
                "修改密码", "账号", "旧密码", "新密码", "确认新密码",
                "两次新密码不一致", "密码修改成功",
                "旧密码不正确", "新密码不能为空", "新密码至少 6 位",
                "新密码不能与旧密码相同", "用户不存在", "用户名不能为空",
                "MES上传成功", "MES返回NG", "MES网络异常/超时",
                "MES反序列化失败", "MES返回数据解析为空", "MES返回数据解析异常",
                "MES 交互实时日志", "清空当前显示", "MES 接口调试界面",
                "库位ID为空", "未查询到相应数据",
                "固化时间: ", "进入时间: ", "库位ID: ",
                "库内总物料:", "静置已完成:", "正在静置:", "明日可完成:",
                "产品号", "产品ID",
                "运行状态", "统计信息",
                // 网关配置窗（漏一个切英文就蹦中文）
                "网关配置", "机台连接", "接口地址",
                "IP地址", "端口", "启用",
                "命令字", "完成位", "心跳地址", "固化时间地址",
                "进入时间地址", "PC时间地址", "库位地址", "库位长度",
                "产品前缀", "产品起始", "产品步长", "产品个数",
                "产品读长", "产品写长", "报警地址",
                "测试连接", "全部测试", "测试中...", "连接成功", "连接失败",
                "保存并重启", "恢复缺省",
                "模拟模式下配置仅对真机有效",
                "测试用表单值直测保存后重启生效",
                "以下配置有误",
                "触发地址", "数据地址", "MSG地址", "DATA地址",
                "状态地址", "完成地址", "数据长度",
                "已保存，重启软件后生效",
                "IP地址无效", "端口范围1-65535", "寄存器地址格式须为R/D+数字",
                "不能为空", "长度范围1-1000",
                "范围0-99999", "范围1-10000", "范围1-1000",
            };
            foreach (string k in keys)
            {
                string en = LanguageService.Tr(k);
                Assert.False(string.IsNullOrWhiteSpace(en), $"空翻译: {k}");
                Assert.NotEqual(k, en); // 中文模式原样返回，英文必须变
            }
            // 出厂缺省机台名有英文对照；现场自定义名（[StationNames]）允许回退原文
            foreach (string name in AppConfig.DefaultStationNames)
                Assert.NotEqual(name, LanguageService.Tr(name));
        }
        finally
        {
            LanguageService.CurrentLanguage = "CH";
        }
    }

    [Fact(DisplayName = "未知键回退原文")]
    public void Unknown_FallsBack_Original()
    {
        LanguageService.CurrentLanguage = "EN";
        try
        {
            Assert.Equal("没见过的词条", LanguageService.Tr("没见过的词条"));
        }
        finally
        {
            LanguageService.CurrentLanguage = "CH";
        }
    }
}
