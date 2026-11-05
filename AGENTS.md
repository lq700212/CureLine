# AGENTS.md — CureLine 项目指南

> 本文件是 AI 助手在操作本项目前的**强制前置阅读**。开工前先读本文件，明确角色、约定与红线。
> 优先级：本文档 > 项目已有代码风格 > 通用最佳实践。
>
> 来源：本项目是从 VB 老项目（`PLC_ReadData` 单机固化收料 + `PLC_ReadDataA`
> 八机通用网关）1:1 复刻的 WPF 融合版；本文件是从 HuaJiVision 项目 AGENTS.md
> 提炼的**通用部分**（编码/提交/注释/线程/日志/配置纪律）+ 本项目专有约定。

## 项目角色

你是本项目（.NET 8 WPF 上位机应用）的**资深维护工程师**，负责按用户需求改代码、
修 bug、沉淀约定。改动必须**可编译、可运行（测试全绿）、风格统一**，关键改动后
更新 `CHANGELOG.md`。

## 技术栈

- **.NET 8 WPF**（`net8.0-windows`，SDK 风格四工程：`Core` 共享类库 + `CureStation`
  程序①固化程控（IO 耦合器程控 + 单线过账）+ `CimGateway` 主程序（程序② CIM 整线
  过账，原名 PlcMesBridge，V0.0.21 改名定名）+ `Tests` xUnit，见 `CureLine.slnx`；
  两程序可分机部署，拆分设计见 `docs/重构设计_20261028.md`）
- 通讯子模块 **`extern/kaleidoscope`**（git submodule，net472，零改动直接引用，
  详见"子模块"节）；机台 Modbus 通讯用其 `libs/HslCommunication.dll`（12.6.0，公司已购，
  不从 NuGet 另引）；授权复用子模块 `HslAuthorization`（`hsl.dat`，见"机密红线"）
- SQLite 用 NuGet **官方 `System.Data.SQLite.Core` 1.0.119**（注意：NuGet 上另有
  非官方 `System.Data.SQLite` 2.x 包，禁止引用）；JSON 用 `Newtonsoft.Json` 13.x
- INI 走 kernel32 P/Invoke（`Infrastructure/IniFile`，读不到回填缺省的自愈语义）

## 仓库与提交

- 远程 `origin = https://github.com/lq700212/CureLine.git`（原 PLC_ReadData，V0.0.24 随项目更名，GitHub 旧名自动重定向），主分支 `main`。
- 入库：源码（`src/`、`tests/`）、`config.sample.ini`、文档（README/CHANGELOG/AGENTS）。
  不入库：`bin/`、`obj/`、运行时落盘（`config/*.ini|*.json`、`DataBase/*.db3`、
  `log|logs|Logs/`、`*.log`）、机密（`hsl.dat`、`*.kcfg`），见 `.gitignore`。
- **不主动 commit/push**，除非用户明确要求；提交前先 `git status` + `git diff`
  确认只包含预期改动。

## 子模块 extern/kaleidoscope（必读）

- 接入方式：`git submodule add https://github.com/lq700212/kaleidoscope.git
  extern/kaleidoscope`，主仓库只记 commit 指针，子模块保持独立版本线。
- **一次 push 双更新**：已配 `push.recurseSubmodules=on-demand`，一条 `git push`
  自动先推子模块再推主仓库。新克隆后跑 `git submodule update --init`。
- **现场修 bug 回流流程**：进 `extern/kaleidoscope` 先 `git checkout main`
  （否则 detached HEAD 提交悬空）→ 改 → commit → push（子模块仓库）→
  回主仓库 `git add extern/kaleidoscope`（更新指针）→ commit → push。
  改完子模块源码必须先用 VS MSBuild 编译通过（`Kaleidoscope.csproj` net472），
  再跑本项目全量测试（引用的是它的 `bin\Debug` 输出）。
- **引用策略（已验证，勿轻易改）**：net8 主工程直接 `Reference`
  其 `bin\Debug\Kaleidoscope.dll` + `libs/` 三个 DLL，`NU1701` 已压制
  （`SubmoduleSmokeTests` 4 例锁定：加载/构造/授权降级/配置模型）。
  若将来引用失效，备选才是按 kaleidoscope AGENTS 迁移方案给它加
  `net8.0-windows` 多目标——那是跨仓库大动作，需用户明确批准。

## 阶段待办（两程序拆分路线，详见 docs/重构设计_20261028.md）

- **P1 基建与骨架（已完成）**：子模块升 V1.8.2 + VM 基建下沉 Core + IO 点表
  家族（`IoPointTable`/`IoPointTableStore`/`IoBus`/连通测试）+ `CureStation`
  程序①骨架（30 仓位主窗 + 点表配置窗 + 自愈 + Mock）+ 测试 156 全绿。
- **P2 程序②改造（进行中，P2.1 已完成）**：P2.1=删三菱整链（`MelsecPlcClient`/
  `SingleMachineCoordinator`/单机视图与"运行模式"菜单/配置窗单机页——
  用户拍板"物理搬运即可"，git 历史可找回）+ 真机底座统一 Modbus TCP
  （`ModbusStationClient`，地址/打包/分帧集中一处）+ 配置模型收敛
  （删 `RunMode`/`[SinglePLC]`/`PLCAscii`）。**P2.2 待做**：6 接口迁移与
  整线联调（待 D/R 终版地址 + CIM W68 终版）。**网关参数已全配置化
  （V0.0.22 补齐最后两处）**：URL/触发字/6 组地址束/DataLength/字节序/
  分段偏移/扫描周期全部 ini 键 + 配置窗可编辑、保存即热生效——W68 终版
  到位后改配置即可，代码零改动（`MesConfig.DataSegmentOffsets`/
  `AppConfig.ScanIntervalMs`）。
- **P3 固化程控（进行中，P3.1 完成 + P3.2 完成）**：**P3.1=仓位状态机 +
  工艺策略**（`Core/Curing/` 三件套 + `CuringStateStore` 断电续算开关；工艺
  策略窗 + 主窗接线）。**P3.2 前半=权限 + 登录 + 工单信息页（已完成，
  V0.0.49 重做三级角色）**：
  `Security/OperatorStore`（operators 表三级 `OperatorRole` operator/admin/
  dev + actor 兜底 + 防锁死）+ 登录窗（纯登录，改密面板已删）+
  工单窗（R/L 双工位，编辑区按角色三档禁用）+ 用户管理窗 +
  主窗 Gate 统一门禁（工单·日志=登录即可/策略·IO·过账·演示·用户管理=
  管理员+/点表=开发者，operator 登录后设置整钮禁用）。
  **P3.2 后半=单线过账（已完成 2026-10-28，
  全配置化）**：`CuringMesOptions`（[CuringMes] 节）+ `CuringMesPoster`
  （托盘到位=进站 API0030 I / 托盘离开=固化时间 API0027+出站 API0030 O /
  工单窗 API0032 物料上传；出站被接受→已生产+1）+ 状态机三事件挂点 +
  过账配置窗（管理员+，保存即热更新）+ 顶栏过账状态。**待现场校准（W68 终版
  到位后配置窗改 URL/口径即可，代码零改动）**：三接口 URL、时间单位、
  进站时机、静态参数实值。**V0.0.20 双固化架（已完成）**：主窗分组布局
  （全局架号每 10 站一组、上下堆叠带标题条，单架视觉不变）+ 库位编码架内
  换算（`EncodeInRack`）+ 模板起始架号 + 跨设备语义查重 + 点表设备高级参数
  全 UI 化 + `[CuringMes] RackSides` 工位映射（`MacNo_L/MacLocNo_L` 分套）+
  `[setting] StringByteOrder` 字节序配置化 + 登录窗重做/权限窗语义色。
  **V0.0.41 多耦合器现场适配 + 全设备连接热更（已完成）**：现场布局落定
  （10 列 × 3 层、每列 1 台町洋 GFNB-1A3A，IP 现值 .20~.29 预置）+
  批量一键生成/自愈改现场口径 + 测试连接补 UI（死命令修复）+ 校验补
  重复 IP/重名闸 + 轮询按块分组读（180→20 帧/周期）+ 通道归属可见 +
  程序②机台连接保存即热重建（RestartApp 链删除）。

## 铁律（违反即返工）

1. **文件编码 UTF-8**。写文件用 write 工具；终端 GBK 显示是假象，
   判定中文内容只用严格 UTF-8 解码读，禁止肉眼判码。
2. **UI 线程禁做网络 IO**：机台通讯/DB/HTTP 全在后台线程（多机 `StationGateway`
   自带线程、程序①总线后台轮询）；TCP 连接必须带超时；事件在工作线程触发，
   UI 订阅方必须 `Dispatcher.Invoke` 回 UI 线程。
3. **读写失败即断连标记 + 节流重连**：真机底座 `ModbusStationClient` 包装
   子模块 `ModbusTcpMasterClient`（断连标记/关坏 socket/5s 节流全在其内部）；
   IO 总线 `ModbusPointTableBus` 同款（未连 5s 节流重连）。勿引入
   "按轮询周期估时"逻辑。
4. **日志只记边沿**：连上/断开/触发/成功/NG 记，连续失败中间静默，防刷屏。
5. **SQL 一律参数化**：禁止字符串拼接 SQL（老项目残留的拼接只允许在
   `CureRecordStore` 内的固定语句，新增查询必须 `@参数`）。
6. **改动后必须构建 + 测试全绿**：`dotnet build` 0 警告 0 错误，
   `dotnet test` 437/437（V0.0.55 全量；V0.0.53 及以前各轮增量明细见各版本
   CHANGELOG，V0.0.33~37 增量未逐版回填此括号——基线此后只增不减，
   V0.0.49 因删改密面板与旧权限窗用例净减 4、自愈播种防回归 +2，
   V0.0.54 软件授权 +18，V0.0.55 策略窗全选升格 +3）；禁止交付编译不过/测试不过的代码。
7. **机密红线**：`hsl.dat`（HSL 授权）只放 exe 同目录，已 ignore，
   **不得入库、不得外泄、不得写进任何文档**；源码禁止出现授权码明文。
   **`LicenseSignKey.dat`（软件激活签名私钥）** 只留厂商签发工具目录，已
   ignore——丢失=已发激活码全部作废，外泄=可伪造任意激活码；禁止入库、
   外发、打进部署包。客户激活码可存 ini，但同样不得写进项目文档/仓库。
8. **配置样例同步**：新增配置键必须同步改 `config.sample.ini` + `AppConfig`
   缺省值（ini 自愈依赖缺省值，漏了现场起不来）。
9. **改干净改彻底（2026-10-29 用户拍板，2026-10-31 重申强化：项目未上线，
   无任何兼容历史包袱）**：**所有修改一律不用考虑兼容老项目/旧版本**——
   废弃的键/样式/字段/属性/接口/枚举/显示口径/配置窗段落一律**直接删除**，
   旧实现残体（不再被任何一方调用的方法/事件/ini 键/XAML 资源）随改动
   **同批清光**；禁止留"兼容旧引用"的占位、空资源、过渡注释、双套并存口径，
   也禁止以"防破坏既有行为/老项目口径/渐进迁移"为由保守保留——默认就地
   删净，无须回问用户。边界：**外部契约不属此列**——MES 报文口径、机台
   寄存器语义、点表键名、配置节名等**现场约定**（"已知坑"小节列明的）仍须遵守。

## 代码约定

- 类/方法/属性 PascalCase；私有字段 `_camelCase`；接口前缀 `I`；事件 PascalCase。
- **注释要详细，让小白能看懂**：每个类头写清"干什么 + 为什么 + 怎么改"，
  边界/自愈/协议细节必须注，杜绝废话注释。中文 UTF-8。
- **MVVM 分层（WPF 层，两程序同款）**：View（XAML 纯绑定 + xaml.cs 只设
  DataContext/转交密码/转发关窗）← ViewModel（各程序私有 VM 在自己
  `ViewModels/`；**基建已下沉共享**：`src/CureLine.Core/ViewModels/` 的
  `ViewModelBase`/`RelayCommand`，namespace 仍为 `CureLine.ViewModels`，
  两工程零感知；后台线程更新经 `UiInvoke`）→ Model/Core（业务，可测）
  + `IDialogService`（VM 开窗/提示唯一出口，测试用 Fake 桩）。**对话框
  接口不下沉**：窗集合是各程序私有资产，各程序自建接口与生产实现
  （主程序 `tests/.../Mocks/FakeDialogService`、程序① `FakeCureDialogService`）。
  VM 禁止直接 new Window/MessageBox/Dispatcher；PasswordBox 密码经事件
  转交（不可绑定，官方 workaround）；关窗确认/重启经服务。
- **XAML 设计时纪律（新建/改动任何 Window/UserControl 必守，否则预览与运行分叉）**：
  根节点加 `xmlns:d/mc/vm` + `mc:Ignorable="d"` +
  `d:DataContext="{d:DesignInstance Type=vm:对应ViewModel, IsDesignTimeCreatable=False}"`；
  所有展示文本绑定（Title/Header/Content/Label/灯色）加 `FallbackValue`
  （取 VM 中文默认文案，`mc:Ignorable=d` 保证运行时零影响；输入框/运行时
  数据不加）；`Visibility` 绑定必须给 `FallbackValue` 且同格多视图只留一个
  `Visible`、其余 `Collapsed`（绑定失败取缺省 Visible 会重叠，即"按钮叠在一起"
  的根因）；数值绑定（如 UniformGrid 列数）同样给 FallbackValue。
  改完关掉重开设计器看一眼，预览≈运行才算完（教训见 CHANGELOG V0.0.11）。
- 业务逻辑进 `Core`（可测）；新增业务分支必须先补 `Tests` 用例
  （模拟机台 + 本地 HttpListener 桩 MES，基建现成）；新增 VM 编排补
  `ViewModelTests` 用例（Fake 服务 + TestScope 隔离）。

## UI 资源来源（各类 UI 资源，2026-10-29 用户拍板）

- **适用范围**：不限于按钮图标——凡 UI 需要的图形资源（按钮/菜单图标、
  装饰元素、控件视觉参考、动效思路等）一律先翻 galaxy 本地库。
- **来源顺序**：
  1. **本地优先**：`E:\Project\galaxy`（uiverse-io/galaxy 仓库本地镜像，按
     Buttons/Cards/Inputs/Tooltips/Checkboxes/Toggle-switches/Loaders 等
     分类存放，每个 HTML 文件是一个自包含组件样例（内嵌 SVG/CSS）——
     直接 grep/打开挑，勿联网拉取已有资源）；
  2. **本地目录不存在时**才从上游 `https://github.com/uiverse-io/galaxy` 获取。
- **WPF 转译纪律**：galaxy 是 HTML/CSS 组件库，取的是图形形状与视觉思路——
  SVG 图标转 XAML `Path`/`Geometry`（或 SVG 资源），**配色一律换成本项目主题
  语义键**（`Brush.*` / `Brush.Fresh.*`，双主题同名异值），禁止照抄样例里的
  硬编码色值（会破坏 Light/Dark 换肤）；尺寸/圆角对齐既有按钮样式
  （`GhostButton`/`FreshButton` 系）。
- **先库后手**：涉及图标/图形资源的任务先 grep galaxy 本地库（如
  `grep -rl '<svg' /e/Project/galaxy/Buttons`）再动手；产出物在提交说明里
  注明来源样例文件名（如 `galaxy/Buttons/xxx.html`），便于回溯与统一替换。

## 关键文件导航

| 文件 | 作用 |
| --- | --- |
| **两程序骨架** | |
| `src/CureStation/` | **程序①固化程控**（新 exe）：30 仓位主窗 + IO 点表配置窗 + 工艺策略窗 + 工单信息窗 + 用户管理窗 + 登录窗（`ViewModels/` 各窗 VM）+ 自建 `DialogService.cs` |
| `src/CimGateway/MainWindow.*` | 主窗：多机网关视图、视图筛选（替代 Form2/Form6；单机视图与"运行模式"菜单已随单机链路删除；Phase 2 演化 CimGateway）；**V0.0.31 无边框自绘**（品牌顶栏=标题条 44：chip"CIM"+标题+时钟 / 按钮组 / 系统三钮，最大化 RootGrid 垫边）+【深色/浅色】主题钮；**V0.0.39 顶栏精简**：连接设备/日志/登录留顶栏，调试/网关设置/修改密码/快捷方式/中英切换/主题收"设置 ▾"下拉（ToggleButton+Popup：StaysOpen=False 点外自关 + IsOpen↔IsChecked 双向绑定 + 菜单项点击 code-behind OnMenuClick 收合；门禁/VM 文案零改动） |
| `src/CimGateway/Themes/` | **程序②双主题**（V0.0.31，与程序①同构不同文件——主题键板是程序私有资产不下沉）：Light/Dark 语义键同名异值字典（只保留本程序有消费者的键）+ `ThemeManager`（ParseTheme 非法回 Light + 测试态防御）；ini `[setting] Theme` 同名键两程序各自 exe 独立生效；`CimGatewayThemeTests` 锁两字典键集合全等 |
| `src/CimGateway/Controls/StationPanel.*` | 机台过账卡片（V0.0.51 业务化重做）：头行名+灯（四态 Gray/Green/Lime/Red/Yellow）+ 今日三数 + 6 接口微表（`ApiStatRow`，失败行 FailRowBg）+ 最新消息行；日志 ListBox 已删（触发流在 `LogLines` 供单机视图） |
| `src/CimGateway/ViewModels/` | 主程序各窗 VM（`IDialogService` 基建 + 各窗 VM，主窗 VM 含视图筛选/门禁 + 主题切换） |
| `src/CimGateway/MesDebugWindow.*` | 调试窗：5 接口表单 + API0033 扩展 + 预览上传 |
| `src/CimGateway/MesLogWindow.*` | 日志窗（-1 全部机台 / >=0 分机台，关窗退订；清空钮=危险色） |
| `src/CimGateway/GatewayConfigWindow.*` | 网关配置窗（机台连接/接口地址两页 + 测试连接 + 保存热生效，逻辑在 Core；单机页已删；V0.0.32 类名去 PLC 化 `PlcConfigWindow`→`GatewayConfigWindow`，VM 同改 `GatewayConfigViewModel`）；V0.0.31 Tab 隐式主题样式 + 测试结果字色走语义名（VM "Ok"/"Err" → View 触发器映射 AlertOkFg/AlertErrFg）；V0.0.32 机台连接页每行含机台名框（[StationNames] 配置化） |
| `src/CimGateway/MesAlarmWindow.*` / `ImageWindow.*` | 报警窗 / 图片查看 |
| **程序② UI 键板** | `App.xaml`（GhostButton/Fresh 弹窗样式族/TabControl+TabItem 隐式主题/WinTitleBar/WinSysBtn + Geo.* 图标，色全 DynamicResource 走 Themes 字典）；全部弹窗统一圆角自绘骨架（WindowStyle=None + WindowChrome + Fx.WinShadow + WinTitleBar 品牌标题条，可缩放窗 ResizeBorderThickness=6）；**登录窗输入左缘对齐口径（V0.0.40 像素实测）**：自定义模板 ScrollViewer 已 Margin=Padding、控件内部又消费一次——文本左缘=框内白+2×左Padding+基础内缩（TextBox 系 7/PasswordBox 3），用户名 TextBox 左 8 对应密码框 FreshPwdInput 左 10 同线；程序①登录窗的对齐锚点是 `PART_EditableTextBox` Margin 8（ComboBox 上挂 Padding 无效——现行 FreshCombo 模板无 Padding 消费点，旧"左 17/左 3"定标已失效，详见程序① 登录窗语义 ⑩，勿互套）；原 `Styles/FreshBlue.xaml` 已删 |
| **Core 共享（固化工艺家族，P3.1 新增）** | |
| `src/CureLine.Core/Curing/CuringPolicy.cs` | 工艺策略模型：时长三级覆盖（仓位>层>全局，`ResolveMinutes` 唯一出口）+ 光栅门控 + `CuringPolicyValidator`（时长/层号/仓位键/重名） |
| `src/CureLine.Core/Curing/CuringPolicyStore.cs` | `config/curingpolicy.json` 存取（策略库+激活名）+ `BuildDefaultBook` 缺省库自愈 |
| `src/CureLine.Core/Curing/CuringStateStore.cs` | `config/curingstate.json` 计时状态持久化（`PersistMinutes` 开关的落盘件；运行时数据非配置，删了即回退重计） |
| `src/CureLine.Core/Curing/WorkOrderStore.cs` | 工单信息 `config/workorder.json`（R/L 双工位+EMP/BC/PT×2，字段名对齐 MES 16 字段）+ 自愈空工单 |
| `src/CureLine.Core/Security/OperatorStore.cs` | 程序①三级操作员账号（V0.0.49 重做：`OperatorRole` Operator/Admin/Dev + 预置三账号 `SeedUser` 播种 + PBKDF2 复用；增删/改密全带 **actor 兜底**，VM 置灰之外的第二道防线）；**防锁死红线**：dev 账号永不可删、密码仅本人可改、Dev 角色不开放创建（预置唯一） |
| `src/CureLine.Core/Curing/SlotCuringMachine.cs` | **仓位状态机**：托盘到位→（门控）→黄灯计时→绿灯放行→离开灭灯；边沿后台直转+1s 心跳 Pump；灯写去重补写/断连恢复对齐/策略热切换三自愈；过账挂点三事件（TrayArrived/CuringStarted/TrayLeft，只发真实边沿） |
| `src/CureLine.Core/Curing/SlotLocationCode.cs` | 客户库位编码换算（Date Matrix，10 站×3 层）：架×层↔编码互转（`Encode`/`TryDecode` 客户约定锚点）+ 双架口径（`RackGroupOf`/`StationInRack`/`EncodeInRack` 按架内站号换算，每台架独立一套 A01~J12），主窗卡片标题与后续扫码绑定的同源入口 |
| **Core 共享（单线过账家族，P3.2 后半新增）** | |
| `src/CureLine.Core/Curing/CuringMesOptions.cs` | 过账配置模型（ini `[CuringMes]` 节）：开关/工位/时间单位/进站时机/中途取件/静态参数五件；`NormSide` 规范化（非 L 按 R 兜底） |
| `src/CureLine.Core/Curing/CuringMesPoster.cs` | **过账服务**：状态机边沿→MES 报文（进站/出站链/物料上传）；出站被接受→已生产+1；门槛双保险（开关+URL）；全 Task.Run 后台+异常兜底；`UpdateOptions` 热更新；`PostingChanged` 顶栏事件（后台线程触发，UI 须封送） |
| `src/CureStation/MesPostConfigWindow.*` + `ViewModels/MesPostConfigViewModel.cs` | 过账配置窗（管理员+）：全参数一窗改，URL 校验→写 ini→`MesConfig` 本进程生效；主窗保存后热更新 |
| `src/CureStation/DialogService.cs` | 生产 `IDialogService` + `CuringMesPosterHolder`（工单窗上传动作的静态桥接点，收在程序①内） |
| **Core 共享（IO 家族，P1 新增）** | |
| `src/CureLine.Core/ViewModels/` | 下沉的 VM 基建（`ViewModelBase`/`RelayCommand`，namespace 不变） |
| `src/CureLine.Core/Comms/IoPointTable.cs` | IO 点表模型 + `PointTableValidator`（重复绑定两道闸）+ `PointTableSnapshot` |
| `src/CureLine.Core/Comms/IoPointTableStore.cs` | `config/pointtable.json` 存取 + `BuildDefaultTemplate` 缺省模板（自愈） |
| `src/CureLine.Core/Comms/IoBus.cs` | `IPointTableBus` + 真机 `ModbusPointTableBus`（逐块读写/RMW 写 DO/5s 重连）+ 演示 `MockPointTableBus` |
| `src/CureLine.Core/Comms/IoCouplerConnectionTester.cs` | IO 耦合器连通测试（三态文案，先测再存） |
| **程序①（策略预置 + IO 测试，V0.0.20 三轮新增）** | |
| `src/CureStation/ViewModels/CuringPolicyViewModel.cs` | 策略窗编排：预置库（IsPreset）/套用目标可选/三拦截/先改副本/「自定义」恒存在（防删+自愈）/改名跟随/可视化预览数据（`SlotPreviewItem` 四档色键）/生效中徽标（`ActiveName`） |
| `src/CureStation/ViewModels/IoTestViewModel.cs` + `IoTestWindow.*` | IO 通讯测试窗：点表驱动仓位卡网格（DI 只读灯/DO 点击钮）+ 一键遍历跑马灯 + 共享总线（关窗 Detach） |
| **Core 共享（软件授权家族，V0.0.54 新增）** | |
| `src/CureLine.Core/Security/LicenseCodec.cs` | **授权编解码单一事实源**（主程序 + 签发工具链接同一份）：双产品设备码（CureLine `CURE` / HJVision `HUAJI`）、挑战值、payload 布局、Crockford Base32、内置公钥验签。payload `[14..15]` 产品 ID（`0x434C`/`0xFFFF`）是两产品隔离的唯一依据，验签后必须由 `LicenseGate` 比对 |
| `src/CureLine.Core/Security/MachineFingerprint.cs` | 本机四因子指纹（MachineGuid + 主板/磁盘/CPU 序列号 → SHA256 前 4 字节）；MachineGuid 缺失直接判采集失败（拒绝出设备码/拒绝激活） |
| `src/CureLine.Core/Security/LicenseGate.cs` | **授权门禁五态**（未激活/试用/永久/已过期/指纹不符）+ 激活落盘 + 产品隔离 + 挑战值防重放 + 三存储防回拨（ini/注册表/SQLite `app_meta`）；全工程放行只许调 `IsProductionAllowed()`；可测实例类 + `Default` 生产单例 |
| `src/CureLine.Core/ViewModels/LicenseActivationViewModel.cs` + 两程序 `LicenseActivationWindow.*` | 激活窗 VM（设备码/状态/原因/激活码输入/命令）+ 两程序各自 XAML（文案产品名不同）；演示模式隐藏入口并禁用命令 |
| `src/CureStation/App.xaml.cs` / `src/CimGateway/App.xaml.cs` | 启动门禁（未授权弹窗，关闭即退出；`ShutdownMode=OnExplicitShutdown` 到主窗建好恢复）+ 每小时巡检 |
| `tools/LicenseIssuer/` | **厂商签发工具**（.NET 8 WPF，不随客户版分发）：GUI 粘贴设备码签发 + `--generate`/`--issue` 命令行；按设备码前缀自动识别产品。私钥 `LicenseSignKey.dat` 已 ignore，禁止入库/外发 |
| **Core 共享（业务既有）** | |
| `src/CureLine.Core/Services/LocalLogService.cs` | 本机操作日志落盘（截 50 字写文件，全量上界面） |
| `src/CureLine.Core/Modes/MultiMachineGateway.cs` | 多机网关：触发→JSON→POST→回写 + Manager（Modbus 底座已接，业务零改动） |
| `src/CureLine.Core/Comms/ModbusStationClient.cs` | **真机底座（P2 新增）**：`IStationClient` 的 Modbus TCP 实现——地址="剥 R/D 取数字"=协议地址、字符串 2 字节/寄存器 big-endian、分帧 125/123，三语义集中一处 |
| `src/CureLine.Core/Comms/`（机台侧） | `IStationClient` + 真机 `ModbusStationClient` + 模拟 `SimulatedStationClient` + 连通测试 `StationConnectionTester`（V0.0.32 去 PLC 化命名，三菱客户端已删；kaleidoscope 外部类型 PlcMasterConfig 等保留原名） |
| `src/CureLine.Core/Mes/` | 模型/配置/日志/HTTP/参数记忆（16 字段）+ `MockMesServer`（内置模拟 MES 桩，仅 Simulate=1 且 URL 空/占位时接管，自配 URL 透传） |
| `src/CureLine.Core/Data/CureRecordStore.cs` | `cure_record` 表 CRUD（V0.0.32 由 PLCtable 改名，项目未上线无兼容包袱）+ `ComputeStats` 纯统计 |
| `src/CureLine.Core/Infrastructure/` | INI/SQLite/路径/语言/启动配置/机台连接配置（`StationConfig.cs`：StationConfigStore/Validator） |
| `tests/CureLine.Tests/` | 434 用例 xUnit（`Mocks/`：本地桩 MES / 网关标准报文 / Fake 对话框两套 / 静态快照隔离 / `ScriptedPointBus` 状态机桩，加用例直接复用；`LicenseTests` 18 例锁双产品授权） |
| `docs/重构设计_20261028.md` | 两程序拆分施工图（边界/点表布局规律/Phase 划分/风险） |
| `tools/DocShot/` | **文档截图 harness（V0.0.50 重作）**：`run_docshot.ps1` 一键 build 两程序→build harness→组装 runner→起 exe 全自动；**完全后台静默纪律（用户拍板）**——所有窗（含两主窗）`ShowActivated=false`+`ShowInTaskbar=false`+Left/Top=-20000 离屏，绝不抢焦点/不进任务栏/不落用户屏幕；**成品口径=RTB 优先**（`ShotRtb` 按 DPI 缩放渲染视觉树——无 DWM 阴影黑边、与窗口位置无关），PrintWindow 降为兜底（离屏下拍出黑边已被用户目检否决）；镜头名与文档引用全对齐（`cs-workorder-operator/admin.png` 三级视角、`cs-main-idle/flow/dark.png` 三帧），万象清单校验走 `check_config.py`（IMG_WHITE/REQUIRED_IMGS 两张表）+`check_docs.py`；**深色帧换装坑**：斜杠 URI `"/Themes/Dark.xaml"` 相对 Application 根（harness=DocShot 组件 404 FATAL）——必须手 Build `pack://…/CureStation;component/Themes/Dark.xaml` 并**按 Source 认出主题槽替换**（盲替 merged[0] 会把主样式字典换没，半坏视觉），`ThemeManager.Current` 私有 setter 反射同步防顶栏按钮文案不符；工单镜头演示数据用种子 `WorkOrderBook` 不落盘（R1 纪律） |

## 构建与验证命令

```powershell
# 全量构建（0 警告 0 错误）
dotnet build CureLine.slnx --nologo
# 全量测试（434/434 全绿，串行，约 20s；含本地 HttpListener 桩 MES 端到端
#   + 模拟链路 VM 全链路用例：按钮→注入→扫触发→POST 内置桩→回写→观测）
dotnet test tests/CureLine.Tests/CureLine.Tests.csproj --nologo
# 厂商签发工具（授权相关改动后同步构建；私钥 LicenseSignKey.dat 不进仓库）
dotnet build tools/LicenseIssuer/LicenseIssuer.csproj --nologo
# 子模块改动后（VS MSBuild，net472，原样沿用上游命令）
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  "extern/kaleidoscope/Kaleidoscope/Kaleidoscope.csproj" `
  /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m
# GUI 冒烟：Start-Process 起 exe，确认进程存活 + config/DataBase/log 生成，再 Stop-Process
#   （程序①起 CureStation.exe，看 config/pointtable.json 自愈落盘 + 30 张仓位卡）
# 模拟联调：config.ini [setting] Simulate=1（两程序同名键，无真机跑全流程）——
#   CimGateway（程序②，exe 同名）直起即模拟 8 机台网关；CureStation 直起即 Mock 总线按仓位流转演示
```

## 已知坑（复刻 heritage，改前必读代码注释）

- 网关 DATA 拆包地址算法**无补零**（`R17900+30=R17930`）；空值写 `" "` 占位；
  失败回写 STATUS=2/MSG=NG。MES 侧约定，勿改。
- 统计"明日"只比 `Day` 数（跨月有误差）、L/R 只看第 4 字符——老项目口径，
  保持一致，修了就是行为分叉。
- ~~多机键名跳号（PLCIP2..7,9,10）~~ **V0.0.32 已解除**（用户拍板项目无 PLC、未上线无兼容包袱）：ini 键中性化连续命名 `StationUse`/`StationIp1..8`/`StationPorts`，数据库 `CureLine.db3`（原 PLCdatabase.db3）、表 `cure_record`（原 PLCtable）；现场删旧 db/ini 旧键无害（自愈缺省）。
- **Modbus 底座三语义（P2.1 起生效，全在 `ModbusStationClient` 一处）**：
  ① 地址="剥 R/D 前缀取数字"=Modbus 协议地址（0x0000 起保持寄存器），
  残缺/超 65535 拒收走失败链，绝不误写寄存器 0——**待真机联调确认
  CIM 侧寄存器表与该假设一致**；② 字符串 2 字节/寄存器 big-endian
  （首字节装高位，"NG"→0x4E47）；V0.0.20 起现场字节序校准**零改代码**——
  config.ini `[setting] StringByteOrder=SwapBytes` 翻转（`SwapStringBytes`
  开关 + `SwapPairs` 字节对交换，Pack/Unpack 纯函数本体与单测不动）；
  ③ 分帧 FC03≤125 / FC10≤123 寄存器，网关 DataLength=799 字节必须分块，
  中间块失败整次失败（与老项目整串写同口径，勿加事务）。
- `config/` 目录 kernel32 不会自动建，`App.OnStartup` 已确保；删库不怕，
  `EnsureDatabase` 自建表。
- 老项目第 1 台设备名英文译错（抄第 2 台的），本项目已修正。
- **IO 总线两坑（P1 起生效，② V0.0.41 更新为分组读）**：① DO 写必须"读-改-写"
  单寄存器位，整寄存器覆盖会误伤同寄存器其它仓位的三色灯；② DI/DO 扫描按
  "块×方向"分组共用一帧（`ScanPlanner.BuildScanPlan` 纯函数：同块同方向
  9 点一帧，10 台现场=20 帧/250ms 周期），块状布局地址不连续（32 地址只用
  16 位），**禁用** kaleidoscope 连续 `ReadAllInputs`。
- **多台耦合器现场语义（V0.0.41 起，全在 `BuildMachineTemplate`/`ScanPlanner`
  一处）**：① 一台机台 = 10 列 × 上中下 3 层 = 30 仓位，每列 1 台町洋
  NEMO GFNB-1A3A（1 块 = 16DI+16DO），**IP 现场现值 192.168.1.20~.29 已
  预置为缺省模板**（自愈/批量生成共用；设备名"IO耦合器1~10"）；②
  GFNB 寄存器占位 DI@0x1000(FC04)/DO@0x2000(FC03·FC06) 已对照官方手册
  6.4/6.5 节核实一致（另有位区 FC02/FC05@0x0000 可用但无需）；③ 多台
  生成器设备 **TimeoutMs=300**——多台串行建连 3s/台会拖首轮 30s（真机
  自愈测试用例 34s 的教训），LAN 300ms 快失败快重连；④ "测试连接/测试
  全部"测的是编辑区草稿值（先测再存），结果列表留最近 50 条；⑤ 校验器
  拦跨设备重复 IP:端口与重名；⑥ **热更红线**：两程序所有连接/参数配置
  保存即生效——程序①点表保存→`StartBus` 整换总线（状态机 Attach 流程
  不丢），程序②网关配置保存→`RestartConnections()`（后台 Dispose 旧
  `MultiMachineManager`→`InitMulti` 重建→复用 `ConnectMulti` 后台重连）；
  **程序化重启链路已删**（`RestartApp`/`IsRestarting`/接口方法/Fake 桩/
  语言 key 全清），勿再以"重启生效"为口径加回；⑦ **点表绑定表直编
  （V0.0.45）+ 设备切换回显**：`SelectedDevice` setter 必须联动
  `SyncDeviceEditor`（设备名/IP/高级参数全套回显——此前漏调的 bug 用户
  目检发现；全删设备 null→null 不触发 setter，手动清编辑区）；绑定表
  **行内直改写穿 Raw**（`PointBindingRow` 可编辑属性即 Raw 的门面，
  无编辑面板草稿——`Edit*` 七属性/ApplyBinding/FlushSelectedToRaw 链
  已删勿加回；物理点/语义键唯一冲突由保存前校验统一拦，行内不重复拦）；
  角色/方向下拉的 ItemsSource 用 `x:Static`（DataGridColumn 不在可视
  树，绑 DataContext 无效）；V0.0.46 补手感三件：绑定表必须显式
  `IsReadOnly="False"`（App.xaml 隐式 DataGrid 样式缺省 True=只读展示
  表口径，不显式覆盖则全列改不了点击无反应）；
  角色/方向列=TemplateColumn+**常驻 ComboBox**（单击直接出下拉——
  DataGridComboBoxColumn 的下拉只见于编辑态，缺省单击只选中像"没反
  应"）；文本列单击即编辑（`PointTableWindow` code-behind
  PreviewMouseLeftButtonDown，缺省双击才进编辑）；加删行双边同步 Raw
  集合与行集合；点号列 V0.0.47 起同行内可编辑（原先只读注释称"加删行
  才是正路"但【＋添加行】固定位号 3、走不通——现场换接线本能换 UI 路径；
  列绑 `Bit` 纯序号，X/Y 前缀语义由方向列表达，`BitText` 派生文本已删）。
   ⑧ **点号列"显示 vs 编辑"两口径（V0.0.48）**：显示=按钮行右端"点号显示"
   **单下拉**（用户拍板简单化设计：一个下拉能表达的不拆"进制+勾选+前缀
   下拉"四件套）六形态——`BitDisplayStyle`（`TerminalXY`=端子号 X8/YF
   缺省丝印口径 / `TerminalIQ`=I/Q IEC / `Hex0x` / `Dec` / `Oct` / `Bin`），
   选择即落盘 config.ini [setting] `PointBitStyle`（非法回缺省，Theme 同款
   自愈；程序①专用 UI 键不进 AppConfig）；编辑=模板列 `BitEdit` 当前形态
   裸数字（端子号口径下带本行方向前缀 X/I 或 Y/Q 也能吃，解析失败弹回
   原值）；越界/重复照旧保存前校验统一拦。**Bit 存储恒为十进制 int**，
   状态机/通讯/校验零感知；点号=块内物理端子位（16 位寄存器位号），误改
   =绑错端子静默失灵——三道闸已有（UI 数字弹回/保存校验/StartBus 兜底），
   语义误改属直编固有风险，用户拍板不加保存前确认弹框，靠"测试连接+
   IO 测试窗"现场验证。
- 点表布局三键可改现场分叉：`DiStartAddress`/`DoStartAddress`/
  `BlockStrideRegisters`（缺省 0x1000/0x1010/0x20，块 K 线性外推）；
  改动必同步 `PointTableTests` 的地址断言。
- `TryEnsureConnected` 建连是同步等待（最多 `TimeoutMs`=3s），会阻塞
  轮询线程一拍——骨架期可接受，P2 提速再改异步。
- **程序② 主窗业务化语义（V0.0.51 起，全在 `MainWindowViewModel`/`StationPanelViewModel` + `MultiMachineGateway` 观测事件）**：① 统计口径=进程内观测累计、重启清零（对账以日志为准，不落盘不做第二本账）；② MES 状态点只反映最近一次报文结果（不做心跳 ping——无触发时"连通"是假象）；③ 卡片灯四态优先级 Red>Gray(未启用)>Yellow(今日有失败)>Lime/Green——Yellow 是观测增强，通讯语义零改动；④ "未启用"态来自"已配置为不连接本工站"日志关键词（AddLog 内聚判定，中英双语关键词）；⑤ 日志窗全量窗（-1）收**全部机台**日志（旧口径只收无主日志与"全部机台"语义不符、窗恒空——已修正勿回改）；`GetAllCachedLogs` 合并 8 台缓存按行首时间戳排序（格式固定字符串序=时间序）；⑥ 演示触发器仅 Simulate=1（随机台随机接口预置最小 JSON+触发字，走真机同链路），真机零影响勿在生产代码调用；⑦ 新 UI 文案一律走 `Tr()`+词典（本次补 6 接口短名+汇总条共 20 余条）；⑧ XAML 触发器死区坑全中招预防：灯色/点色/消息底色的缺省值全部写进 Style Setter；
⑨ **模拟触发按钮 + 内置模拟 MES 桩（V0.0.52，全在 `MockMesServer`+VM）**：顶栏
「模拟触发」仅 `Simulate=1` 渲染（真机模式 Visibility 折叠、命令也有 `_useSim`
双保险），单击注入一击=预置 JSON+触发字置 1，机台×接口按 `_mockTick` 轮转
（连点 6 下覆盖 6 接口），未连接时代替用户连接（`_simClients` 空且
`ConnectEnabled` 才触发）、连接中忽略；`_simClients` 是 (Id,Client) 元组表
（卡片日志归属要机台号），别退回裸客户端表；**桩接管规则**：`Simulate=1` 且
URL 为空或含占位主机 `192.168.120.129`（=MockMesServer.PlaceholderHost，即
config.sample.ini 缺省样例）才指向内置桩（端口 5012~5021 逐试，全败回 null
退"URL 未配置"失败链），**自配 URL 一律透传**、桩地址只填内存静态值不写
config.ini（网关配置窗保存会 AppConfig.Load 刷回 ini 原值，故
RestartConnections 后要重调 FillPlaceholderUrls）；桩 NG 规则：报文
`RC_NO="NG"` 回 STATUS=2/MSG=NG（演示拒绝→报警窗）；Simulate=1 下 VM 构造
即自动连接（演示开箱即亮）；⑩ **测试静态隔离红线**：直接改写 MesConfig/
AppConfig 静态值的测试类必须挂 TestScope（MesLayerTests 曾因无隔离把
`http://x/30` 泄漏给后续测试，模拟链路用例首当其冲半数概率挂——已补）；模拟
链路用例装配显式清空 6 URL，超时转储带计数+失败原因+Http Error 原文。
- **主窗仓位网格（V0.0.19 起，V0.0.20 加双架分组）**：① 10×3 最小下限
  （`BuildSlots` 的 `Math.Max` 垫底，常量与 `SlotLocationCode` 同源）——
  点表只配 1 架也铺 30 张卡，属展示口径勿当 bug 报；② 集合**行优先**
  （先层后架）是布局契约——乱序会"层"错行，改前先看
  `MainVm_Slots_RowMajorOrder` 用例（注意：双架时平铺 `Slots` 是"整层跨架
  连续"（先层后架），主窗实际渲染走 `RackGroups` 的**组内**行优先，两序不同
  勿混）；③ 卡片主标题=客户库位编码（Date Matrix）——V0.0.20 起走
  `EncodeInRack`（按**架内站号**换算，每台架各自一套 A01~J12），`Encode`
  本体（客户约定锚点）不动，扫码绑定也走架内口径；④ 默认最大化启动 + 卡片
  无固定宽高（拉伸均分），改布局别把固定 Width/Height 加回去；⑤ **双架分组
  契约**（V0.0.20）：全局架号每 10 站切一组（`RackGroupOf`），主窗绑
  `RackGroups`（组标题"N号固化架"+组内 10×3+标题条仅组数>1 显示），外层
  `UniformGrid Rows=GroupCount` 纵向均分；组内卡与平铺 `Slots` 同批实例；
  ⑥ **XAML 触发器死区（V0.0.46 修徽章死文案的血泪）**：Style 触发器想
  改的属性（卡状态胶囊/状态词的 `Text`、副文的 `Visibility`），缺省值
  必须写进 Style Setter——写在元素标签上是**本地值，优先级高于 Style
  触发器**，触发器永远不生效（徽章橙底+写"空闲"的根因：底色 Border
  的 Background 没写本地值所以活了，TextBlock 的 Text 写了所以死）；
  排查口诀："触发器改不动＝找标签上的本地值"。
- **状态机语义四键（P3.1 起，P3.1 补丁加第④条，全在 `SlotCuringMachine` 一处）**：
  ① 流程"到位即计时"——托盘 OFF 即回空闲灭灯（中途取件计时作废记日志），
  红灯状态机不写（留异常语义）；② 计时状态**缺省不持久化**——断电/重启后托盘在架
  的仓位重新计时（宁可重计也不放行没固化够的货）；**策略勾 `PersistMinutes`
  才持久化**（`config/curingstate.json`，状态变化即全量重写，重启按记录续算，
  断电时间计入固化时长；CuringStateStore，删文件即回退重计）；③ 策略热切换：
  新到位仓位开计时现取当前策略（激活即热更）；在制仓位默认按开计时那一刻
  锁定的时长走完，**宿主收口调 `ApplyPolicyHotChange`（V0.0.58）做单向热更
  ——只延不缩**：新解析值比锁定值大才延长并同步重写 curingstate.json，调小
  一律不动（调小立即生效=自动放行没固化够的货，工艺红线；Gate/Done 不参与）；
  持久化恢复也用记录里的锁定时长（热更延长后记录同步更新）；④ WaitingGate
  不落盘——没开计时，重启重走门控。改流程先改 `SlotCuringMachine` 头注释与
  `SlotCuringMachineTests` 断言。
- **权限门禁语义（V0.0.49 三级角色起生效，全在 `OperatorStore` +
  `MainViewModel.Gate` 一处）**：① 账号体系两程序各自独立——程序①
  `operators` 表三级 `OperatorRole`（Operator 操作员 < Admin 管理员 <
  Dev 开发者），主程序 `users` 表 admin/dev，互不相干（分机部署）；
  ② 预置三账号：operator/123456、admin/123456、dev/dev123（App.OnStartup
  三参 `EnsureSeeded`，**播种直写库走私有 `SeedUser`**——`AddUser` 对
  Dev 角色有"不开放创建"边界，会拦预置装配曾致 dev 播不进库，勿再绕回
  AddUser 播种）。**自愈播种语义（V0.0.49 收尾实测定）**：App 每次启动
  都调且幂等无害——先清 level 存 `Level1~4` 文本的旧口径残行（老库会占住
  预置名致 operator/dev 种不进、登录失败；未上线无兼容包袱），再对预置名
  **缺谁补谁**、在册者不动（不覆盖运行期改过的密码——`Seed_` 两用例锁定）；
  上线必改，文档不得记录现场真实密码；
  ③ **防锁死红线（换口径）**：dev 账号**永不可删**（谁删都拒）、密码只有
  dev 本人可改、Dev 角色不开放创建（预置唯一）——系统永远留最高权限钥匙；
  "改级别"功能已删（换角色=删了重建），`SetLevel` 勿再出现；
  ④ 未知/脏 level 文本按操作员兜底（安全侧：错降无害错升危险）；旧库存量
  Level1~4 文本 `FromText` 就地映射（L4→Admin、L1~3→Operator）——纯防老
  测试库崩，勿据此新增"兼容老分级"逻辑；
  ⑤ 门禁矩阵（三级）：工单/日志=登录即可；工艺策略/IO 测试/过账配置/
  演示开关/用户管理=管理员+；点表配置=开发者专属且 **admin 登录后设置
  下拉里直接没有该项**（`ShowPointTableItem` → XAML DataTrigger 收起项，
  不是置灰）；演示开关收管理员的理由：Mock 边沿会照常驱动状态机与过账链
  （影响生产数据口径）——改门禁只动 `MainViewModel.Gate` 调用处；
  ⑥ **operator 登录后设置整钮禁用**（`IsSettingsEnabled` → XAML 半透明
  触发器）——不是逐项隐藏，勿拆成逐项控制；
  ⑦ **operator 无改密权**（登录窗改密面板已删勿加回）：改密全走用户管理窗
  （`AccountManagerWindow`，替代原"权限管理窗"）由 admin/dev **重置**
  （免验原密码，立即生效；新密码 ≥6 位）；admin 可重置 operator+admin
  （含自己）、dev 可重置所有人（含自己）；用户管理窗操作方法开头有
  CanXxx 兜底（RelayCommand 直调 Execute 不判灰，程序态调用也拦）+
  Store 层 actor 兜底（AddUser/RemoveUser/SetPassword 全带 actor 参数），
  双层防线勿拆；
  ⑧ 工单窗 Flush 有门禁兜底：级别不够的字段回读盘上原值（防绕过 IsEnabled
  直改 VM 属性越权落盘）；编辑权三档（V0.0.49）：`CanEditDaily` 数量/清零
  （operator+）、`CanEditRoute` 途程、`CanEditProcess` 制程+上传使能
  （admin+）；
  ⑨ **登录窗记忆语义（V0.0.20 对齐 mFormLogin 的保留部分）**——记住密码
  复用 `RunModeSettings`（DPAPI 密文存 config.ini [setting]，SavedUser +
  RememberToken_<账号>，ini 永无明文），用户名一改就跟出该账号记忆（命中
  回填+勾选，未命中清空+取消勾）；改密面板删除后"改密成功同步记忆"链路
  已不存在，记忆只在登录成功时保存；用户名下拉=在册账号实时表（ListUsers），
  不是登录历史文件；登录密码框双向接线（VM→框回写靠"值不同才走"防互踢），
  键盘链统一在 View 层 PreviewKeyDown（按钮禁用 IsDefault/IsCancel——
  下拉展开时回车/Esc 放行）；`ListUsers` 排序走 case 表达式定权
  （Dev>Admin>Operator——level 现存文本，字母序与权限序不一致，勿改回
  `order by level desc`）；
  ⑩ 顶栏登录态格式="角色短名：账号"（`ShortName`，如"管理员：admin"，
  不带 LEVEL 字样——UI 口径 V0.0.20 二轮定）；登录窗下拉
  编辑框/下拉项/密码框三处文本左缘对齐是 UI 契约（V0.0.50 实测重定）：
  **编辑框文本左缘=FreshCombo 模板 `PART_EditableTextBox` Margin 8 定标**——
  ComboBox 上挂 Padding 无效（现行模板无 Padding 消费点，旧"左 17"口径已
  失效勿回写）；**下拉项直接继承 App.xaml 隐式 ComboBoxItem Padding 8,4**
  （Popup 边框1+Padding2+Item8=11 与编辑框同线；登录窗本地
  ItemContainerStyle Padding=19 覆盖已删，那是旧模板的左拉补丁，方向相反
  曾致下拉项文本右偏约 13px）；密码框对齐契约不变且动输入框内边距须
  放大截图核对——`FreshPwdInput` Padding 左 3
  是实机像素定标值（PasswordBox 内部渲染无 TextBoxView 内缩，同抄
  FreshInput 的 6 会让密码首字符偏右约 3 逻辑 px，V0.0.30 校准）；
  顶栏连接**短词+悬停全文**两段口径（V0.0.37 推翻"全文不截断"——用户
  目检拍板顶栏抓重点，长文堆栏面挤爆还截不完整）："ConnectionText"=短词
  （`DeriveConnectionShort` 派生：起停/配置/演示态原样，连通="已连接"，
  部分台在线保"N/M 台已连"，全断/一目连接错误的全文="未连接"）；
  `ConnectionDetailText`=详情全文（总线摘要/设备错误串）只走悬停
  ToolTip——错误长文永远不写回顶栏文本，勿再回滚全文字口径。
- **策略窗时间单位语义（V0.0.53 起，V0.0.56 删秒档+主窗联动，真源在
  `DurationUnit`（src/CureStation/ViewModels/DurationUnit.cs）+ 两 VM 各自
  消费）**：① 页面可切 **时/分**（秒档已删——用户拍板没有秒级的固化；
  `PolicyTimeUnit` 枚举 Hours=0/Minutes=1，存量 ini 值 "Seconds" 不在新枚举
  内开机自愈回 Minutes），**仅显示/输入口径**——`CuringPolicy` 的 int 分钟
  存储、状态机计时、断电续算、过账时间单位一律不动；
  ② **策略窗↔主窗联动（用户拍板）**：策略窗 `TimeUnit` setter 唯一写点 →
  `DurationUnit.Change()` 落盘记忆 + 广播 `Changed` → 主窗 MainViewModel 订阅
  → 全部卡片 `RefreshUnitText()`（数值/进度不动只换字面，状态词"剩X"用
  `_lastCuring` 缓存重拼）+ 统计条"⚡码·剩X"重拼——**不等 500ms 拍**；重开
  策略窗/主窗走 `LoadFromIni()` 重读（盘上为准）。勿回滚成"两窗各读各的"；
  ③ **小时口径（"智能省略零段"）**：产物在 `DurationUnit.Text`（整串：
  "2小时0分"→"2小时"、"1小时28分"、"0小时47分"→"47分"（0 段
  省略）、0 兜底"0分"）与 `DurationUnit.SplitBig`（卡片大数字四段交替：
  数字段 26 号、单位字"小时/分"11 号=与"分"后缀同规格——V0.0.56 二轮用户
  拍板；第二段组仅 >1 小时有值，卡内显隐走 `RemainHasTail`）；分钟口径保持
  旧显示（大数字纯数字+"分"后缀、副文"N 分"）；卡片文本
  `RemainNum1/RemainUnit1/RemainNum2/RemainUnit2/RemainHasTail/TotalDisplay`
  只换字面，`RemainMin/TotalMin/ProgressPct` 恒分钟数值（勿把小时数写进数值属性）；
  ④ 切换时策略窗编辑区/仓位行文本按"旧单位解析→分钟→新单位格式化"重写
  （`0.####` 精度整分钟无损往返，`TryParseDisplay` 支持小数、AwayFromZero
  四舍五入到整分钟）；画布数字/ToolTip、浮层摘要、图例四档、Expander 标题
  全走 VM 派生属性，勿在 XAML 写死时间文案；
  ⑤ **架号/层号恒十进制解析**（`ParseIndexOrError`，不走单位换算——否则小时
  模式下架号 3 被当 180 分钟绑错仓位，V0.0.53 实测坑）；
  ⑥ 选择记忆 `[setting] PolicyTimeUnit`（程序①专用 UI 键，非法回 Minutes，
  与 PointBitStyle 同款自愈）；**文本拼装全收 UI 层**：Core `SlotStatus`
  已无 `StateText`、`SlotStatistics.NextClose` 返回最小项不给全文——加显示
  场景勿回 Core 拼带单位的串；
  ⑦ **窄卡缩放（V0.0.56 三轮，用户拍板"等比缩放"）**：卡片大数字行与副文
  行各外包 `Viewbox`（`Stretch=Uniform`+`StretchDirection=DownOnly`）——
  宽度够原样 26/11 号、不够整行等比缩；勿改成缩字号/换行方案（已否决）。
- **策略窗关窗链（V0.0.53 修，勿回退）**：保存/激活/套用成功 → VM
  `CloseDialog=true` + `RequestClose(true)` → View（`CuringPolicyWindow`
  订阅 DataContextChanged）设 `DialogResult=true` 关窗 → 主窗刷新激活策略并
  提示"已保存"。**缺这条链时窗口不关、ShowDialog 恒 false、主窗不刷新不提示，
  用户会误判"添加策略没保存"**（V0.0.53 用户实测反馈的根因）；失败路径不发
  RequestClose。落盘走 `TrySave`（异常弹可见原因），且先落盘成功才
  `ApplyDraftToSelected`（写盘失败不污染内存策略）。
- **策略预置/套用语义（V0.0.20 三轮起；V0.0.57 用户拍板「自定义恒显 +
  套用目标可选 + 开窗即显生效策略」，全在 `CuringPolicy.IsPreset` +
  `CuringPolicyViewModel`）**：① **「自定义」恒存在**（唯一系统缺省策略）：
  删除一律拦（弹提示给出路）+ 载入库缺它时自愈补回（缺省 120 分钟插队首，
  与 BuildDefaultBook 同口径；只补内存，保存时自然落盘）——预置模板有
  缺省落点、激活名失效时 ResolveActive 兜底回它，程控不停摆；② 预置策略
  （IsPreset=true，出厂模板）可选中查看/编辑，但保存/激活/删除一律 VM 层
  拦下——唯一出口【套用所选模板】，**套用目标可选（V0.0.57）**：底部
  "套用目标"下拉=全部非预置策略（「自定义」+用户自建），缺省「自定义」；
  编辑内容**整体替换**写入目标并**激活目标**（不残留旧覆盖行）；名单/选中
  由 `SyncApplyTargets` 在载入/增删/保存/套用后重算（目标被删兜底回
  「自定义」，永指向存在的策略）；③ 非预置策略可自由【激活并生效】任意
  一条；**改名跟随**：仅保存被改名的策略恰是当前生效策略时，激活名同步
  换新名（否则激活名悬挂、重启 ResolveActive 兜底回缺省 120，现场时长
  凭空变）；④ 打开窗口缺省选中当前生效策略（ResolveActive），列表项
  "生效中"徽标=本项名==`ActiveName`（INPC 属性 + XAML MultiBinding +
  `StringEqualsConverter` 比对——Core 模型不背 UI 态）；⑤ 老版本 JSON 无
  IsPreset 字段反序列化 false=全按自定义，兼容零迁移；⑥ 保存链=先改副本：
  BuildDraftFromEditor 产草稿→**草稿替换选中项进库校验**→过了才
  ApplyDraftToSelected+落盘（改名撞车不污染原对象，对齐 AgingTestSystem
  大扫荡教训）；⑦ 可视化画布（**V0.0.43 起是主编辑区，"点格子直接改"；
  V0.0.44 起策略编辑面板与每仓位覆盖行表整体删除**——职能全被画布+浮层
  替代，四项参数（名/全局/门控/断电续算）收一行参数条、每层覆盖折叠进
  左列，`AddSlotRow/RemoveSlotRow/SelectedSlotRow` 链已删勿加回，
  `SlotRows` 集合保留=画布写值的数据模型；**清除覆盖=ClearSelectionOverride
  （删行恢复跟随层/全局，设了能删闭环）**；**格子定尺寸 64×58 +
  ItemsControl 定宽 PreviewGridWidth=列数×68（VM 属性）——尺寸不随窗口/
  文字浮动，想调改 Border Width/Height 与 68 步进配套改）**：
  SlotPreviewItems=RackCount×LevelCount 格（与主窗网格同源，DialogService
  从点表算最大架/层传入，下限 10×3），编辑任一时长 RebuildPreview 实时反映；
  格色三态：普通=时长四档（绿<60/黄/橙/红≥180）、**特殊=主题蓝 Accent**
  （IsSpecial 按 SlotMinutes 覆盖字典**存在性真识别**，非数值推断——覆盖值
  恰好等于层/全局时仍算特殊）、选中=FgStrong 描边（与特殊蓝并存）。
  画布交互：**左键单击=选格浮层输时长、左键拖框=框选（橡皮筋透明只描边、
  Ctrl 叠加）、Ctrl+单击=加选/减选、点空白/Esc=清选择、中键拖动=平移
  （左键已让给选择）、滚轮缩放、双击空白复位（格子上双击不复位）、
  右键适应图像**。
  选择真源=VM `_selectedKeys` 键集合（格子对象每次 RebuildPreview 全新
  重建引用，选中态按键恢复——勿依赖 item.IsSelected 自身）；浮层应用=
  `ApplySelectionCommand` 逐格写 SlotRows（有则改无则增）→ 行通知自动
  重建；**全选应用=改全局（V0.0.55 用户反馈）**：选中格铺满画布（架×层
  一个不少）时【应用】升格为设全局时长——同步编辑区全局框 + 清空仓位/层
  覆盖（"每格同值"语义上就是全局，否则 30 条冗余覆盖把值盖住、全局框停
  旧值看着"不同步"）；非全选照旧写仓位覆盖行、全局不动（用例锁定
  `PolicyVm_CanvasApplySelectAll_PromotesToGlobal` 等 3 例）；
  浮层焦点必须 `Dispatcher.BeginInvoke`（鼠标 up 内同步 Focus 被
  WPF 点击聚焦覆盖）。缩放/平移/命中矩形在 View 层（TransformToVisual
  宽高乘 scale），业务零耦合（CuringPolicyWindow.xaml.cs）；
  ⑧ **保存即三重热更反馈（V0.0.58，收口在 `MainViewModel.LoadPolicyBook`）**：
  保存返回 true → 主窗重读策略库 → ① 顶栏 `PolicyText` 带全局时长
  （"策略：自定义 · 全局120分"，随显示单位换口径——`RefreshPolicyText`
  是 LoadPolicyBook 与 OnDisplayUnitChanged 双入口，勿再散写）；
  ② `SlotCuringMachine.ApplyPolicyHotChange` 在制单向上调（状态机四键③）；
  ③ 空闲卡"计划 X"预览下一拍（≤500ms）立见新值（`RackSlotViewModel.PlanText`，
  仅 Idle 有值、策略未知="计划 —"，非空闲态不给——托盘在架/灯在亮有各自
  副文或无计划语义）。此前"改全局时长主界面没更新"的观感根因=②不可行
  （在制调小=放行未固化货，工艺红线）+①③缺反馈，勿回退顶栏纯名字口径。
- **IO 通讯测试窗语义（V0.0.20 三轮，全在 `IoTestViewModel`）**：① 通道
  清单完全来自点表（Role≠Reserved、架层>0），仓位卡补占位保持三格齐整
  （Exists=false 不响应点击）——**换项目零代码**，勿写死通道表；② 共享
  主窗 `IPointTableBus`（不另建 TCP），WritePoint 一律 Task.Run 后台
  （内部 TCP IO，UI 线程禁网络铁律），失败灯弹回快照真值不误导；③ 一键
  遍历=500ms/路跑马灯（只动本拍两路，同寄存器其它位靠总线 RMW 保位），
  停止自动全 DO 关闭；④ 总线 Error/ConnectionChanged 后台线程触发——
  订阅处立即 UiInvoke 封送，**窗口 Closed 必须 Detach()**（停计时器/
  停遍历/退订，防悬挂订阅）。
- **单线过账语义（P3.2 后半起生效，全在 `CuringMesPoster`/`CuringMesOptions`
  一处）**：① 触发映射=老项目命令字——托盘到位=进站（API0030 I）、托盘
  离开=出站链（先 API0027 外层数组后 API0030 O）、出站被接受（STATUS=1）
  →已生产+1（0027 失败只报警不挡累计）；② **只发真实边沿**——重启对齐
  （AlignFromSnapshot）不发三事件，防 MES 重复过账；③ 失败只记日志+顶栏
  状态，**不弹窗不重传**（老项目写机台报警位+弹窗，差异已登记）；④ 门槛
  双保险=开关开+URL 非空，关时不产生网络请求；⑤ **锁内事件红线**：状态机
  三事件在 _sync 锁内触发，处理器禁止同步回调状态机公共方法（重入死锁），
  只做轻量投递；⑥ 物料上传（API0032）按**按钮工位**取槽（不走全局配置
  Side——自动进/出站才用配置工位）；⑦ WaitingGate 离开在 Arrive 口径下补
  出站（固化 0 秒，有进必有出），Start 口径没发进站则出站也不发；⑧ BC_NO=
  工单批次物料条码（批次级过账），板级产品 ID 上报走程序②整线网关（架构
  差异已登记）；⑨ **双架工位映射**（V0.0.20）：自动进/出站按全局架号查
  `[CuringMes] RackSides`（"11-20:L" 架号段:工位，非法段忽略，空=全用
  Side），`MAC_NO/MAC_LOC_NO` 随工位取（L 工位可选 `MacNo_L/MacLocNo_L`，
  留空回落主值）；解析走 `SideOfRack` 使用时现算不缓存（配置窗保存是 new
  实例直赋属性的路径，缓存会漏更新）。改口径先改 `CuringMesPoster` 头注释
  与 `CuringMesPosterTests` 断言；配置键名勿改（现场已按样例部署）；
  ⑩ **双工位开关（V0.0.38，现场实拍无双工位→缺省关）**：`[CuringMes]
  DualSide` 0=单工位——`SideOfRack` 恒回 Side（RackSides 残留配置也忽略，
  防把唯一工单引到空 L 槽）、工单窗只显 Side 侧一套面板且文案全去 R/L
  字样（**视图↔槽映射恒定** R↔Right/L↔Left，Sync/Flush 不判开关——隐藏
  侧数据原样往返）、顶栏过账文本不带工位字样；1=老双架双面板口径全量
  回归（映射+Mac*_L+R/L 两枚上传钮）。过账配置窗勾选（管理员+/热生效）。
  **主窗底部工单带同口径（V0.0.47 补齐——V0.0.38 漏网）**：双工位 R/L 两卡并排
  （`WoCardColumns`=2）；单工位只显 Side 侧一张卡（`ShowWoCardR/L`，列数=1 独占
  整宽）+ 去"R/L 工位·"字样、隐藏卡数据照常刷新；顶栏文案与工单带显隐统一走
  `MainViewModel.ApplyPostOptions`（ctor + 配置窗热更唯一出口，勿再散写两份）；
  ⑪ **"过账工位"下拉仅双工位勾选时显示**（V0.0.42，用户拍板：单工位常显
  R/L 选择易误会要配双工位）——隐藏但 `EditSide` 照常 Sync/Flush，ini
  `[CuringMes] Side` 缺省 R 原样往返、`SideOfRack` 语义不变，勿把该属性
  当死代码删。
- **软件授权语义（V0.0.54 起，全在 Core/Security 三件套 + 两程序 App）**：
  ① **放行唯一口**=`LicenseGate.IsProductionAllowed()`（试用/永久/演示=true，
  未激活/过期/指纹不符=false）——业务层别自己读 ini 判断；② **双产品隔离**：
  CureLine 与 HJVision 共用同一把 ECDsa 私钥，靠激活码 payload `[14..15]`
  产品 ID 区分（CureLine=0x434C / HJVision=0xFFFF）——验签通过只证明"码是
  厂商签的"，`LicenseGate` 必须再比对产品 ID，否则会串用；设备码前缀
  CureLine=`CURE`、HJVision=`HUAJI`（校验位哈希含前缀，HUAJI 旧码逐字节
  兼容）；③ **挑战值防重放**：激活窗每次打开 `NewChallenge()` 换随机值，
  `TryActivate` 比对 payload 挑战值；已生效授权 `Refresh` 只验签+指纹，
  不比对挑战值（重开窗不影响已激活机器）；④ **防回拨三存储**：ini
  `[License]/WaterMark` + `HKCU\Software\CureLine\License` + SQLite
  `app_meta` 取最大，再与 `LicenseGate.BuiltOn`/exe 文件时间取大做下限，
  容差 1 小时；单点删改挡不住，三处全删还有 exe 时间兜底；⑤ **启动门禁**：
  两程序 `App.OnStartup` 先 `Refresh`，未授权 `ShowLicenseActivation`，关闭
  仍未授权即 `Shutdown`；启动期 `ShutdownMode=OnExplicitShutdown`、主窗
  建好后恢复 `OnLastWindowClose`（CimGateway 已删 `StartupUri` 手工建主窗）；
  运行期每小时巡检；⑥ **演示模式** `[setting] Simulate=1` 免激活（`Refresh`
  直接判 Permanent 并置 `IsSimulated`，UI 隐藏激活入口/命令双保险）——
  仅联调，不得用于生产；⑦ **私钥红线** `LicenseSignKey.dat` 只留厂商签发
  工具目录，已 ignore；丢失=已发码全部作废，外泄=可伪造任意激活码；换钥
  必须同步替换两产品 `LicenseCodec.PubKeyB64` 并重编；⑧ **测试纪律**：
  `LicenseGate.Default` 会采集真机指纹，单测一律构造实例注入假指纹/内存
  水位/临时密钥对，不要调 `Default.Refresh`；`LicenseTests` 18 例覆盖双产品
  设备码/隔离/挑战重放/回拨/到期/演示模式；
  ⑨ **换码切换语义（V0.0.60 起，全在 `LicenseGate.TryActivate` 一处）**：
  已激活（Trial/Permanent）状态下再输入一枚合法新码即覆盖切换授权类型
  （限期↔永久双向），落盘覆盖旧记录 + `Refresh` 内即生效——**热更：无需
  重启、无需先清授权**；两主窗 VM 订阅 `LicenseGate.Default.StateChanged`
  刷顶栏授权文案并记操作日志边沿（StateChanged 调用线程=UI 线程，直接赋值
  无需封送；程序① Dispose 对称退订）；激活窗重开即换新挑战值——拿旧设备
  码签的码被拦"设备码已刷新"属**防重放语义**，勿放开；换码必须用窗口当次
  显示的设备码重新签发（三个换码用例锁定：降级/升级/降级后水位不误伤到期）；
  ⑩ **开发自动授权（V0.0.60 起，仅 #if DEBUG 二进制存在，全在
  `LicenseGate` "开发自动授权"区段一处）**：Debug 编译的 exe 在生产单例
  `Default` 上自动注入 90 天内存态试用授权（挂 `Refresh` 尾部
  `ApplyDevLicenseIfUnlicensed`；真实判定坏态才覆盖、不落盘不碰水位三存储、
  每次重算"今天+90 天"，可测实例经 ReferenceEquals 判定零影响）——开发机
  免输激活码。**发布必须 Release 编译**（客户版整段剔除，反编译无后门），
  Debug exe 禁止外发；手测真实激活/换码/过期流程时设环境变量
  `CURELINE_DEVLICENSE=off`（0/false 同义）关闭注入（该开关同样只在 Debug
  二进制存在）。

## 文档同步（每次任务收尾自查）

- **`CHANGELOG.md`**：关键改动记一笔（范围 + 为什么）。
- **`README.md`**：新增功能/可见行为/配置键任一变化即同步。
- **`docs/重构设计_20261028.md`**：两程序拆分架构演进（Phase 状态/点表布局）变化即同步。
- **`config.sample.ini`**：新增配置键同步（见铁律 8）。
- **代码注释**：中文 UTF-8，小白能看懂，写完自查编码。
- **提交前自检**：`git status` + `git diff`；用户不要求 commit 时只留工作区改动。
