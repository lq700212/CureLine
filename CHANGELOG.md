# CHANGELOG — PlcMesBridge

> VB 老项目复刻融合版，两程序拆分演进中。版本规则：关键行为变化即记一笔。

## V0.0.13（2026-10-28，两程序拆分 P1：子模块升级 + CureStation 固化程控骨架）

背景：现场 IO 方案定为 IO 耦合器（Modbus TCP 从站，替代 PLC 直读触发），
业务拆两个独立程序（①固化程控 ②CIM 整线过账）。设计文档
`docs/重构设计_20261028.md` 承载全部点表布局规律与 Phase 路线。本轮为
P1（基建与骨架）交付。

- 子模块 extern/kaleidoscope 85811e9 → f53d14b（V1.8.2）；并在子模块内
  新增 `ModbusTcpIoController.ReadInputRegisters(ushort, ushort)`（FC04
  任意地址，与 `ReadHoldingRegisters` 对称）——IO 总线逐块读 DI 的原始
  方法（子模块存量连续读不适用于块状布局；**该方法未 commit，随本轮请示**）。
- VM 基建下沉共享：`ViewModelBase`/`RelayCommand` 移到
  `src/PlcMesBridge.Core/ViewModels/`（namespace 不变，两工程零感知）；
  对话框接口不下沉——窗集合是各程序私有资产，程序①自建 `IDialogService`。
- Core 新增 IO 点表家族（`Comms/`）：`IoPointTable`（模型 + 校验器：
  物理点/语义键重复绑定两道闸）+ `IoPointTableStore`（JSON 持久化 +
  缺省模板生成）+ `IoBus`（真机 `ModbusPointTableBus`：逐块 FC04/FC03 各读
  1 寄存器、DO 写走读-改-写防误伤同寄存器其它通道、5s 节流重连、状态差分
  边沿；演示 `MockPointTableBus`：托盘/光栅仓位流转 + 演示掉线重连）+
  `IoCouplerConnectionTester`（三态连通诊断）。布局落死电气 IO 表：块 K
  线性外推 DI=0x1000+(K-1)×0x20、DO=DI+0x10；块内每 4 位一组=1 层
  （输入 0=左光栅/1=右光栅/2=托盘，输出 0=红/1=黄/2=绿）；起始地址/
  步进/取反三键可配，现场分叉代码零改。
- 新程序①骨架 `src/CureStation`：主窗 30 仓位卡（10×3 起步，跟随点表
  扩展）+ 连接状态卡；点表配置窗（设备增删/一键缺省模板/绑定行增删改/
  测试连接/保存校验——点点鼠标，现场不改代码）；点表自愈（缺 → 模板
  落盘，坏 → 错误列表 + 模拟兜底）；工程挂 `PlcMesBridge.slnx`。
- 演示开关：`config.ini [setting] Simulate=1` 同名键沿用到程序①。
- 测试 133 → 156（新增点表/IO 总线/程序① VM 用例；基线只增不减）；
  GUI 冒烟通过（CureStation.exe 存活 + pointtable.json 自愈落盘）。
- 验证：构建 0 警告 0 错误，测试 156/156。

## V0.0.12（2026-09-23，XAML 设计时纪律入 AGENTS）

- AGENTS.md 代码约定新增"XAML 设计时纪律"（V0.0.11 教训固化）：
  新建/改动任何 Window/UserControl 必带 `d:DataContext` +
  展示文本 `FallbackValue` + `Visibility`/数值 `FallbackValue`
  （同格多视图只留一个 Visible），改完重开设计器确认预览≈运行。
- 验证：文档改动，无需构建测试（V0.0.11 已验证构建 0 警告 0 错误，测试 133/133）。

## V0.0.11（2026-09-23，XAML 设计时预览修复）

- 修设计器预览与运行不一致：全仓 XAML 纯 `{Binding}` 且无设计时
  DataContext，设计器里 DataContext 为空→绑定全失败→控件无文本；
  主窗单机/多机两视图绑定失败后取 Visibility 缺省值 Visible 而非折叠，
  在同一格重叠，按钮看起来"叠在一起"（运行因 VM 注入真值故正常）。
- 10 个 XAML（9 窗 + StationPanel）加 `d:DataContext`（DesignInstance，
  IsDesignTimeCreatable=False，只给智能提示）+ 展示文本补
  `FallbackValue`（取各 VM 中文默认文案，预览≈运行；mc:Ignorable=d，
  运行时零影响）；单机视图 FallbackValue=Visible、多机视图
  FallbackValue=Collapsed，设计器只显单机视图不再重叠；
  UniformGrid 列数 FallbackValue=4。
- 验证：构建 0 警告 0 错误，测试 133/133。

## V0.0.10（2026-09-23，设置下拉修复 + 弹窗居中）

- 修设置下拉子项全空、点不了：子项在 Popup 独立视觉树里，
  `RelativeSource AncestorType=Window` 全灭（Header/Command 绑不上），
  改直接绑 VM（DataContext 走逻辑树继承不受影响），dev 的 PLC设置
  同一根因一并恢复。新增 `FreshMenuSubItem` 子项样式（悬停整行变蓝白字，
  与顶栏药丸呼应；未登录置灰靠它）。
- 弹窗统一屏幕居中：`DialogService` 加 `Prep` 给所有新窗挂主窗 Owner
  （CenterOwner 有主可依；非模态窗关主窗跟着收不残留），
  `MesLogWindow` 补 `CenterScreen`（之前唯一缺启动位置的）。
- 验证：人工测试通过（设置下拉文本/高亮/可点、dev 三项、弹窗居中）。

## V0.0.9（2026-09-23，全量 MVVM：View 绑定 + VM 编排 + 服务开窗）

- 确认旧架构不是 MVVM（全仓无 ViewModel/INPC/ICommand/DataContext），
  9 个窗 + 1 控件一次迁完，无双风格尾巴；Core 业务与行为零变化。
- 新 `ViewModels/` 基建：`ViewModelBase`（INPC + `UiInvoke` 跨线程封送，
  后台节拍/网关更新属性不跨线程崩）+ `RelayCommand` +
  `IDialogService`（VM 开窗/提示唯一出口，生产 `DialogService`，
  测试 `FakeDialogService` 桩）。
- 主窗 VM 化最重：双视图绑定显隐、160 行产品表、8 面板 VM、后台节拍、
  时钟 Timer、菜单门禁全进 `MainWindowViewModel`；xaml.cs 只剩
  DataContext + 转发关窗。PLC 窗三页改 ItemsControl 绑定
  （行内按钮经 RelativeSource 找父 VM 命令）；PasswordBox 经事件转交
  （不可绑定的官方 workaround）；`StationPanel` 的 VM 由主窗注入。
- 测试 126→133：新增 `ViewModelTests` 7（登录失败/跟随、模式保存、
  主窗门禁拦放、退出清态、改密码规则/代改显隐；Fake 服务 +
  TestScope 隔离）；测试工程增引主工程（只用 ViewModels，不建窗）。
- 验证：构建 0 警告 0 错误，测试 133/133，UIA 端到端
  （启动→dev 登录→菜单回显→设置下拉三项）通过。

## V0.0.8（2026-09-23，密码真正安全：SQLite 账号体系 + PBKDF2 + DPAPI）

- 明文密码彻底清零：代码里不再有密码常量（`AdminPassword`/`DevPassword`
  删除），ini 里不再存明文（旧 `RememberLogin`/`SavedPwd` 系键启动时清扫）。
- 新 `Core/Security/`：`PasswordHasher`（PBKDF2-SHA256 12 万次迭代 +
  独立盐 + Pepper，恒定时间比对；新密码≥6 位、不得与旧相同）、
  `UserAccountStore`（`users` 表与 PLCtable 同库，全参数化；
  空表播种一次，已改密码永不覆盖；未知用户与密码错同回 None，防枚举）、
  `RememberMeProtector`（记住密码走 Windows DPAPI CurrentUser 加密，
  P/Invoke crypt32 零新包，ini 只存 Base64 密文）。
- 登录窗校验改走账号体系；"设置"下拉新增"修改密码"（自己改自己，
  dev 可下拉代改他人）；App 启动播种 + 清扫旧明文键。
- 初始密码只在 App 启动播种处出现一次，上线前必须修改，文档不记录明文。
- 测试 116→126：新增 `SecurityTests` 9（盐唯一/验对错/规则/播种/
  全拒绝/库无明文/改密全流程/代改/重播不覆盖/DPAPI 往返），
  记忆用例加"ini 无明文""旧键清扫"2 例。
- 验证：构建 0 警告 0 错误，测试 126/126，GUI 冒烟通过。

## V0.0.7（2026-09-23，菜单栏：运行模式独立 + 设置下拉 + 双账号登录）

- 菜单栏新增"运行模式"独立按钮：登录后点击弹模式切换窗
  （单机/多机二选一，保存写 ini，变了自动重启；逻辑与旧设置窗内切换一致）。
- "设置"改为下拉：admin 见"修改密码" + "创建桌面快捷方式"，
  dev 另见"PLC设置"（PLC 配置窗直开，保存后自动重启；
  未登录点子项先提示登录）。
  修 bug：顶菜单小药丸样式把 MenuItem 模板换成纯 Header、无 Popup，
  有子项也展不开——"设置"改用带 Popup 的 `FreshMenuDropButton` 样式
  （同外观 + 右下箭头 + 子项下拉，UI 自动化真实点击验证展开正常）。
- 新增菜单栏"登录"按钮：两档账号 admin（普通管理）、dev（最高权限）；"记住密码"按账号各记各的，用户名框改谁就跟出谁的密码；
  已登录再点可退出或切换账号，按钮回显当前用户名。
- 旧 `SettingsWindow` 删除（模式部分由 `ModeSwitchWindow` 接替，
  快捷方式逻辑搬进主窗 `MiShortcut_Click`）；`RunModeSettings` 删密码常量
  与全部老记忆键兼容分支（项目未上线，不留迁移包袱；
  登录校验后移 SQLite 账号体系，见 V0.0.8）。
- 测试 115→116：登录用例改角色断言，新增记住密码跟随账号
  （分账号存取、取消只清自己）1 例；中英覆盖在原用例内追加 7 新键。

## V0.0.6（2026-09-23，程序图标三件套）

- 图标归位 `src/PlcMesBridge/Assets/`（根目录 `app.ico`/`app.png` 迁入；
  `app.ico` 256 程序图标，`app.png` 800 高清源备用）：exe 文件/任务栏图标
  走 csproj `ApplicationIcon`，8 个窗体左上角走 `Icon="Assets/app.ico"` 资源。
- 设置窗新增"创建桌面快捷方式"：一键建桌面 `.lnk`（图标取 exe 内嵌图标，
  换图标重编即跟新；已存在不覆盖），逻辑进 Core（`DesktopShortcut` 纯函数 +
  WScript late-bound COM，可测），WPF 只搬运。
- 测试 111→115：`DesktopShortcutTests` 4（命名/兜底/图标指向/新建+不覆盖，
  临时目录真建 `.lnk`，不碰真实桌面）；`LanguageCoverageTests` 追加 3 新键。
- 验证：构建 0 警告 0 错误，测试 115/115，GUI 冒烟通过。

## V0.0.5（2026-09-23，主窗菜单栏布局）

- 主窗操作入口按模式分家：单机顶菜单（日志/调试/语言/设置）+
  连接蓝按钮进状态卡放心跳灯右侧；多机顶菜单留四项，
  仅连接设备按钮进第二行（+ 视图 + 扫描周期）；时钟并入菜单栏右侧。
  双模式真实主窗离屏渲染目检通过。
- 单机左列改为纵向状态带 → 统计带 → 日志（原来按钮区位置），右产品表
  高度拉满（行高 30）；多机顶栏只留视图筛选 + 扫描周期。
- 顶菜单项换肤 `FreshMenuButton`（白底小药丸 + 浅蓝边 + 深蓝字，
  悬停变蓝底白字），离屏渲染目检通过。
- 状态带 / 统计带分区卡片化：浅蓝 `FreshHintBorder` 运行状态卡 +
  白底 `FreshCardBorder` 统计卡（新增复用样式键）+ 深蓝小标题，
  真实主窗离屏渲染目检通过。
- 验证：构建 0 警告 0 错误，测试 111/111，GUI 冒烟通过。

## V0.0.4（2026-09-23，PLC 可配置：现场零写代码）

- 新增 PLC 配置窗（三页：单机PLC / 多机PLC / 接口地址）：
  设置窗 → "PLC配置..." → 填表 → "测试连接"（拿表单值直测，不用先保存）
  → "保存并重启"（先校验，错当场拦）→ 主窗自动重启生效。
- 单机 11 处硬编码地址收敛进 `SinglePlcConfig`（IP/端口/命令/完成/心跳/
  固化/进入/PC时间/库位/产品区算法参数/报警位），缺省=老项目硬编码，
  老 ini 无新键行为零变化；报警位读链：新键 → 老 `MESPLCALarm` → D30013。
- 多机端口（老项目写死 6060）与协议（老项目写死 1/2/5 号 ASCII）进 ini
  （`PLCPorts`/`PLCAscii`），改后点"连接设备"重连生效；触发/地址束改后
  下次扫描（200ms）即生效。
- 新增 `PlcConnectionTester`（连通 + 读回验证，超时 3s，后台跑不冻界面）。
- 测试 95→111：`PlcConfigTests` 10（缺省/自愈/往返/校验/读链）/
  `CoordinatorCustomAddrTests` 3（换 D500xx 全链路，防写回硬编码）/
  `ConnectionTesterTests` 3（成功/失败链）；中英覆盖扩展 PLC 新键。
- 验证：构建 0 警告 0 错误，测试 111/111，GUI 冒烟通过。

## V0.0.3（2026-09-22，全量走查 + 测试加固 42→95）

- 走查修 4 个 bug：
  （1）`AppConfig.Load` 的 `DataLength` 用 `int.Parse`，ini 手改错一个字符
  启动即崩——改为容错解析，非法/非正数回填 799；
  （2）`IniFile.ReadInt/ReadByte` 自愈按 `"#0.000"` 写（如 "799.000"），
  下次自己解析不了——改为纯整数写，自愈一次就好（`ReadDouble` 保持三位小数）；
  （3）`StationGateway.ShiftAddr` 大小写敏感 + `int.Parse` 无保护——改为
  大小写不敏感、非法地址返回原值不抛（合法输入与老项目逐字一致）；
  （4）`MultiMachineManager.ConnectAll` 失败网关也进 `Stations`——改为当场
  释放、不进列表（`Stations` 只收连上且线程已起的）。
- `LocalLogService` 从 WPF 层搬进 `Core/Services`（纯逻辑无界面依赖，
  按"业务进 Core"约定；`MainWindow` 只改 using，行为不变）。
- `MesLogger` 新增 `ClearForTests()`（仅清内存缓存，供测试隔离，生产不调）。
- 测试 42→95（新增 53 例，全部串行约 7s）：沉淀 `tests/.../Mocks/`
  （`MesStubServer` 本地桩 MES + `PlcScenarios` 标准报文/单机预置 +
  `TestScope` 静态全局快照恢复 + 落盘重定向临时目录，下次直接复用）；
  新增 `AppConfigTests` 7 / `IniNumberFormatTests` 4 / `ShiftAddrRobustTests` 4 /
  `MesLoggerTests` 5 / `LocalLogServiceTests` 4 / `SingleMachineEdgeTests` 9
  （桩 MES 精确断言进站 ACTION=I、出站两次 POST）/ `GatewayExtendedTests` 10
  （0031 拆包/HTTP500/空 URL/空格占位/双触发单次处理）/ `SimAndHttpTests` 8 /
  `LanguageCoverageTests` 2（UI 常用键英文全覆盖）。
- 验证：`dotnet build` 0 警告 0 错误，`dotnet test` 95/95（连跑两轮稳定），
  GUI 冒烟（exe 存活 + config 自愈 + 建库）通过。

## V0.0.2（2026-09-22，界面设置切换模式）

- 主窗新增"设置"按钮（单机/多机视图各一）：点击先弹登录窗
  （本地防误触门禁），通过后弹设置窗。
- 设置窗单机/多机二选一，保存写入 `config.ini [setting] Mode`，
  模式变了主窗自动重启（先拉新进程再退当前进程，并跳过"是否退出"确认，
  否则确认框卡住旧进程会双开；没变只提示已保存）。
- 登录窗密码下方加"记住密码"：勾选存账号到 ini 下次预填，取消即清。
- 逻辑进 Core（`RunModeSettings` 纯函数：登录校验/解析/落盘/重启判断/登录记忆），
  新增 `RunModeSettingsTests` 9 例，测试 42/42。
- 换肤：天蓝色小清新全局样式（`Styles/FreshBlue.xaml`，对标 AgingTestSystem：
  白底+微软雅黑+DodgerBlue 主按钮+浅蓝提示条），同行控件统一上下居中。

## V0.0.1（2026-09-21，复刻基线）

- 单机/多机融合成一个程序，`config.ini [setting] Mode=Single/Multi` 切换；
  新增 `[setting] Simulate=1` 内存模拟联调（无真机跑全流程）。
- 复刻范围：单机版 Form1（含 4 分支命令字、160 行产品表、统计、进出站 MES）、
  多机版通用网关（6 触发→JSON→POST→回写，含 0030/0031 拆包）、调试窗
  （追加 API0033）、报警窗、日志窗（单机全量/分机台）、图片查看。
- 补齐（相对老项目残缺）：Hsl 引用转正（走子模块 `libs`，不再靠 bin 残留）；
  Form2/Form6 死窗体改为视图筛选下拉；D22xx 注释代码不复刻。
- 修正：（1）第 1 台设备名英文误译（抄第 2 台）；（2）节拍移后台线程
  （老项目 UI 线程跑 160 次 PLC 读会冻界面）；（3）连接按钮后台化；
  （4）`SQLiteHalperObj` 拼写修正为 `SqliteHelper`；（5）统计短 ID 归 R
  （老项目会抛异常）。
- 通讯：`extern/kaleidoscope` 子模块零改动直引（net472→net8 已验证），
  `push.recurseSubmodules=on-demand` 一次 push 双更新；HSL 授权复用子模块
  `hsl.dat` 机制；SQLite 用官方 `System.Data.SQLite.Core` 1.0.119。
- 测试 33 例全绿：冒烟4/基础6/统计4/单机7/MES层6/网关4（本地桩 MES 端到端）/管理2。
