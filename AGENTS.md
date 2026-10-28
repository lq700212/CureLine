# AGENTS.md — PlcMesBridge 项目指南

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
  程序①固化程控（IO 耦合器程控 + 单线过账）+ `PlcMesBridge` 主程序（Phase 2
  演化成程序② CimGateway：6 接口整线过账）+ `Tests` xUnit，见 `PlcMesBridge.slnx`；
  两程序可分机部署，拆分设计见 `docs/重构设计_20261028.md`）
- 通讯子模块 **`extern/kaleidoscope`**（git submodule，net472，零改动直接引用，
  详见"子模块"节）；PLC 协议用其 `libs/HslCommunication.dll`（12.6.0，公司已购，
  不从 NuGet 另引）；授权复用子模块 `HslAuthorization`（`hsl.dat`，见"机密红线"）
- SQLite 用 NuGet **官方 `System.Data.SQLite.Core` 1.0.119**（注意：NuGet 上另有
  非官方 `System.Data.SQLite` 2.x 包，禁止引用）；JSON 用 `Newtonsoft.Json` 13.x
- INI 走 kernel32 P/Invoke（`Infrastructure/IniFile`，读不到回填缺省的自愈语义）

## 仓库与提交

- 远程 `origin = https://github.com/lq700212/PLC_ReadData.git`，主分支 `main`。
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
  `SingleMachineCoordinator`/单机视图与"运行模式"菜单/PLC 配置窗单机页——
  用户拍板"物理搬运即可"，git 历史可找回）+ 真机底座统一 Modbus TCP
  （`ModbusStationClient`，地址/打包/分帧集中一处）+ 配置模型收敛
  （删 `RunMode`/`[SinglePLC]`/`PLCAscii`）。**P2.2 待做**：6 接口迁移与
  整线联调（待 D/R 终版地址 + CIM W68 终版）。
- **P3 固化程控（进行中，P3.1 完成 + P3.2 前半完成）**：**P3.1=仓位状态机 +
  工艺策略**（`Core/Curing/` 三件套 + `CuringStateStore` 断电续算开关；工艺
  策略窗 + 主窗接线）。**P3.2 前半=四级权限 + 登录 + 工单信息页（已完成）**：
  `Security/OperatorStore`（operators 表四级 L1~4 + 防锁死）+ 登录窗 +
  工单窗（R/L 双工位，编辑区按级别禁用）+ 权限管理窗 + 主窗 Gate 统一门禁
  （工单 L1/策略 L3/点表·权限 L4）。**P3.2 后半待做**：单线过账（复用 MES 层
  与 16 字段参数记忆，工单字段名已对齐 RC_NO/PT_NO/EMP_NO/BC_NO；物料上传
  使能动作在此接线；等 CIM W68 终版）。

## 铁律（违反即返工）

1. **文件编码 UTF-8**。写文件用 write 工具；终端 GBK 显示是假象，
   判定中文内容只用严格 UTF-8 解码读，禁止肉眼判码。
2. **UI 线程禁做网络 IO**：PLC/DB/HTTP 全在后台线程（多机 `StationGateway`
   自带线程、程序①总线后台轮询）；TCP 连接必须带超时；事件在工作线程触发，
   UI 订阅方必须 `Dispatcher.Invoke` 回 UI 线程。
3. **读写失败即断连标记 + 节流重连**：真机底座 `ModbusStationClient` 包装
   子模块 `ModbusTcpMasterClient`（断连标记/关坏 socket/5s 节流全在其内部）；
   IO 总线 `ModbusPointTableBus` 同款（未连 5s 节流重连）。勿引入
   "按 PLC 轮询周期估时"逻辑。
4. **日志只记边沿**：连上/断开/触发/成功/NG 记，连续失败中间静默，防刷屏。
5. **SQL 一律参数化**：禁止字符串拼接 SQL（老项目残留的拼接只允许在
   `CureRecordStore` 内的固定语句，新增查询必须 `@参数`）。
6. **改动后必须构建 + 测试全绿**：`dotnet build` 0 警告 0 错误，
   `dotnet test` 201/201（P3.1 增 36 例 + P3.2 前半增 17 例；基线此后只增不减）；
   禁止交付编译不过/测试不过的代码。
7. **机密红线**：`hsl.dat`（HSL 授权）只放 exe 同目录，已 ignore，
   **不得入库、不得外泄、不得写进任何文档**；源码禁止出现授权码明文。
8. **配置样例同步**：新增配置键必须同步改 `config.sample.ini` + `AppConfig`
   缺省值（ini 自愈依赖缺省值，漏了现场起不来）。

## 代码约定

- 类/方法/属性 PascalCase；私有字段 `_camelCase`；接口前缀 `I`；事件 PascalCase。
- **注释要详细，让小白能看懂**：每个类头写清"干什么 + 为什么 + 怎么改"，
  边界/自愈/协议细节必须注，杜绝废话注释。中文 UTF-8。
- **MVVM 分层（WPF 层，两程序同款）**：View（XAML 纯绑定 + xaml.cs 只设
  DataContext/转交密码/转发关窗）← ViewModel（各程序私有 VM 在自己
  `ViewModels/`；**基建已下沉共享**：`src/PlcMesBridge.Core/ViewModels/` 的
  `ViewModelBase`/`RelayCommand`，namespace 仍为 `PlcMesBridge.ViewModels`，
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
  （模拟 PLC + 本地 HttpListener 桩 MES，基建现成）；新增 VM 编排补
  `ViewModelTests` 用例（Fake 服务 + TestScope 隔离）。

## 关键文件导航

| 文件 | 作用 |
| --- | --- |
| **两程序骨架** | |
| `src/CureStation/` | **程序①固化程控**（新 exe）：30 仓位主窗 + IO 点表配置窗 + 工艺策略窗 + 工单信息窗 + 权限管理窗 + 登录窗（`ViewModels/` 六个 VM）+ 自建 `DialogService.cs` |
| `src/PlcMesBridge/MainWindow.*` | 主窗：多机网关视图、视图筛选（替代 Form2/Form6；单机视图与"运行模式"菜单已随单机链路删除；Phase 2 演化 CimGateway） |
| `src/PlcMesBridge/Controls/StationPanel.*` | 机台面板复用件（MVVM View，VM 由主窗注入，8 实例） |
| `src/PlcMesBridge/ViewModels/` | 主程序各窗 VM（`IDialogService` 基建 + 各窗 VM，主窗 VM 含视图筛选/门禁） |
| `src/PlcMesBridge/MesDebugWindow.*` | 调试窗：5 接口表单 + API0033 扩展 + 预览上传 |
| `src/PlcMesBridge/MesLogWindow.*` | 日志窗（-1 全部机台 / >=0 分机台，关窗退订） |
| `src/PlcMesBridge/PlcConfigWindow.*` | PLC 配置窗（多机/接口两页 + 测试连接 + 保存重启，逻辑在 Core；单机页已删） |
| `src/PlcMesBridge/MesAlarmWindow.*` / `ImageWindow.*` | 报警窗 / 图片查看 |
| **Core 共享（固化工艺家族，P3.1 新增）** | |
| `src/PlcMesBridge.Core/Curing/CuringPolicy.cs` | 工艺策略模型：时长三级覆盖（仓位>层>全局，`ResolveMinutes` 唯一出口）+ 光栅门控 + `CuringPolicyValidator`（时长/层号/仓位键/重名） |
| `src/PlcMesBridge.Core/Curing/CuringPolicyStore.cs` | `config/curingpolicy.json` 存取（策略库+激活名）+ `BuildDefaultBook` 缺省库自愈 |
| `src/PlcMesBridge.Core/Curing/CuringStateStore.cs` | `config/curingstate.json` 计时状态持久化（`PersistMinutes` 开关的落盘件；运行时数据非配置，删了即回退重计） |
| `src/PlcMesBridge.Core/Curing/WorkOrderStore.cs` | 工单信息 `config/workorder.json`（R/L 双工位+EMP/BC/PT×2，字段名对齐 MES 16 字段）+ 自愈空工单 |
| `src/PlcMesBridge.Core/Security/OperatorStore.cs` | 程序①四级操作员账号（operators 表 L1~4 + PBKDF2 复用）；**防锁死红线**：最后一个 L4 删/降拒绝 |
| `src/PlcMesBridge.Core/Curing/SlotCuringMachine.cs` | **仓位状态机**：托盘到位→（门控）→黄灯计时→绿灯放行→离开灭灯；边沿后台直转+1s 心跳 Pump；灯写去重补写/断连恢复对齐/策略热切换三自愈 |
| **Core 共享（IO 家族，P1 新增）** | |
| `src/PlcMesBridge.Core/ViewModels/` | 下沉的 VM 基建（`ViewModelBase`/`RelayCommand`，namespace 不变） |
| `src/PlcMesBridge.Core/Comms/IoPointTable.cs` | IO 点表模型 + `PointTableValidator`（重复绑定两道闸）+ `PointTableSnapshot` |
| `src/PlcMesBridge.Core/Comms/IoPointTableStore.cs` | `config/pointtable.json` 存取 + `BuildDefaultTemplate` 缺省模板（自愈） |
| `src/PlcMesBridge.Core/Comms/IoBus.cs` | `IPointTableBus` + 真机 `ModbusPointTableBus`（逐块读写/RMW 写 DO/5s 重连）+ 演示 `MockPointTableBus` |
| `src/PlcMesBridge.Core/Comms/IoCouplerConnectionTester.cs` | IO 耦合器连通测试（三态文案，先测再存） |
| **Core 共享（业务既有）** | |
| `src/PlcMesBridge.Core/Services/LocalLogService.cs` | 本机操作日志落盘（截 50 字写文件，全量上界面） |
| `src/PlcMesBridge.Core/Modes/MultiMachineGateway.cs` | 多机网关：触发→JSON→POST→回写 + Manager（Modbus 底座已接，业务零改动） |
| `src/PlcMesBridge.Core/Comms/ModbusStationClient.cs` | **真机底座（P2 新增）**：`IPlcClient` 的 Modbus TCP 实现——地址="剥 R/D 取数字"=协议地址、字符串 2 字节/寄存器 big-endian、分帧 125/123，三语义集中一处 |
| `src/PlcMesBridge.Core/Comms/`（PLC 侧） | `IPlcClient` + 真机 `ModbusStationClient` + 模拟 `SimulatedPlcClient` + 连通测试 `PlcConnectionTester`（三菱 `MelsecPlcClient` 已删） |
| `src/PlcMesBridge.Core/Mes/` | 模型/配置/日志/HTTP/参数记忆（16 字段） |
| `src/PlcMesBridge.Core/Data/CureRecordStore.cs` | `PLCtable` CRUD + `ComputeStats` 纯统计 |
| `src/PlcMesBridge.Core/Infrastructure/` | INI/SQLite/路径/语言/启动配置/PLC 配置模型与校验 |
| `tests/PlcMesBridge.Tests/` | 201 用例 xUnit（`Mocks/`：本地桩 MES / 网关标准报文 / Fake 对话框两套 / 静态快照隔离 / `ScriptedPointBus` 状态机桩，加用例直接复用） |
| `docs/重构设计_20261028.md` | 两程序拆分施工图（边界/点表布局规律/Phase 划分/风险） |

## 构建与验证命令

```powershell
# 全量构建（0 警告 0 错误）
dotnet build PlcMesBridge.slnx --nologo
# 全量测试（201/201 全绿，串行，约 8s；含本地 HttpListener 桩 MES 端到端）
dotnet test tests/PlcMesBridge.Tests/PlcMesBridge.Tests.csproj --nologo
# 子模块改动后（VS MSBuild，net472，原样沿用上游命令）
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  "extern/kaleidoscope/Kaleidoscope/Kaleidoscope.csproj" `
  /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m
# GUI 冒烟：Start-Process 起 exe，确认进程存活 + config/DataBase/log 生成，再 Stop-Process
#   （程序①起 CureStation.exe，看 config/pointtable.json 自愈落盘 + 30 张仓位卡）
# 模拟联调：config.ini [setting] Simulate=1（两程序同名键，无真机跑全流程）——
#   PlcMesBridge 直起即模拟 8 机台网关；CureStation 直起即 Mock 总线按仓位流转演示
```

## 已知坑（复刻 heritage，改前必读代码注释）

- 网关 DATA 拆包地址算法**无补零**（`R17900+30=R17930`）；空值写 `" "` 占位；
  失败回写 STATUS=2/MSG=NG。MES 侧约定，勿改。
- 统计"明日"只比 `Day` 数（跨月有误差）、L/R 只看第 4 字符——老项目口径，
  保持一致，修了就是行为分叉。
- 多机键名跳号（`PLCIP2..7,9,10` 对应 0..7 号机）历史沿用，勿"整理"。
- **Modbus 底座三语义（P2.1 起生效，全在 `ModbusStationClient` 一处）**：
  ① 地址="剥 R/D 前缀取数字"=Modbus 协议地址（0x0000 起保持寄存器），
  残缺/超 65535 拒收走失败链，绝不误写寄存器 0——**待真机联调确认
  CIM 侧寄存器表与该假设一致**；② 字符串 2 字节/寄存器 big-endian
  （首字节装高位，"NG"→0x4E47），现场字节序校准只改 `PackAscii/UnpackAscii`；
  ③ 分帧 FC03≤125 / FC10≤123 寄存器，网关 DataLength=799 字节必须分块，
  中间块失败整次失败（与老项目整串写同口径，勿加事务）。
- `config/` 目录 kernel32 不会自动建，`App.OnStartup` 已确保；删库不怕，
  `EnsureDatabase` 自建表。
- 老项目第 1 台设备名英文译错（抄第 2 台的），本项目已修正。
- **IO 总线两坑（P1 起生效）**：① DO 写必须"读-改-写"单寄存器位，
  整寄存器覆盖会误伤同寄存器其它仓位的三色灯；② 逐块 FC04/FC03 各读
  1 寄存器（子模块原始 `ReadInputRegisters`），块状布局地址不连续
  （32 地址只用 16 位），**禁用** kaleidoscope 连续 `ReadAllInputs`。
- 点表布局三键可改现场分叉：`DiStartAddress`/`DoStartAddress`/
  `BlockStrideRegisters`（缺省 0x1000/0x1010/0x20，块 K 线性外推）；
  改动必同步 `PointTableTests` 的地址断言。
- `TryEnsureConnected` 建连是同步等待（最多 `TimeoutMs`=3s），会阻塞
  轮询线程一拍——骨架期可接受，P2 提速再改异步。
- 主窗仓位网格有 10×3 最小下限（`MainViewModel.BuildSlots` 的 `Math.Max`
  垫底）——点表只配 1 架也铺 30 张卡，属骨架期展示口径勿当 bug 报。
- **状态机语义四键（P3.1 起，P3.1 补丁加第④条，全在 `SlotCuringMachine` 一处）**：
  ① 流程"到位即计时"——托盘 OFF 即回空闲灭灯（中途取件计时作废记日志），
  红灯状态机不写（留异常语义）；② 计时状态**缺省不持久化**——断电/重启后托盘在架
  的仓位重新计时（宁可重计也不放行没固化够的货）；**策略勾 `PersistMinutes`
  才持久化**（`config/curingstate.json`，状态变化即全量重写，重启按记录续算，
  断电时间计入固化时长；CuringStateStore，删文件即回退重计）；③ 策略热切换只
  影响新到位的仓位，进行中的按开计时那一刻锁定的时长走完（持久化恢复也用
  记录里的锁定时长，不用当前策略）；④ WaitingGate 不落盘——没开计时，重启
  重走门控。改流程先改 `SlotCuringMachine` 头注释与 `SlotCuringMachineTests` 断言。
- **权限门禁语义（P3.2 前半起生效）**：① 账号体系两程序各自独立——程序①
  `operators` 表四级 L1~4，主程序 `users` 表 admin/dev，互不相干（分机部署）；
  ② **防锁死红线**：最后一个 LEVEL.4 删/降一律拒绝（`OperatorStore` 层拦，
  权限窗只负责弹框展示）；③ 未知级别文本按 L1 兜底（安全侧：错降无害错升
  危险）；④ 门禁级别缺省：工单 L1（途程/数量/清零 L2、制程代码 L3）、
  工艺策略 L3、点表/权限管理 L4——改门禁只动 `MainViewModel.Gate` 调用处；
  ⑤ 工单窗 Flush 有门禁兜底：级别不够的字段回读盘上原值（防绕过 IsEnabled
  直改 VM 属性越权落盘）；⑥ 初始密码 admin/123456 只在 App.OnStartup 一处，
  上线必改，文档不得记录现场真实密码。

## 文档同步（每次任务收尾自查）

- **`CHANGELOG.md`**：关键改动记一笔（范围 + 为什么）。
- **`README.md`**：新增功能/可见行为/配置键任一变化即同步。
- **`docs/重构设计_20261028.md`**：两程序拆分架构演进（Phase 状态/点表布局）变化即同步。
- **`config.sample.ini`**：新增配置键同步（见铁律 8）。
- **代码注释**：中文 UTF-8，小白能看懂，写完自查编码。
- **提交前自检**：`git status` + `git diff`；用户不要求 commit 时只留工作区改动。
