# PlcMesBridge（WPF 融合版）

从 VB 老项目（`PLC_ReadData` 单机固化收料 + `PLC_ReadDataA` 八机通用网关）
融合重构的 .NET 8 解决方案，含两个可独立部署的 WPF 程序：

* `src/PlcMesBridge` — 主程序：MES 多机网关（八机 Modbus TCP 统一底座；
  Phase 2 演化为程序② CIM 整线过账，单机模式已删）
* `src/CureStation` — **程序①固化程控**：IO 耦合器（Modbus TCP）30 仓位程控——
  点表"点点鼠标"配置 + 主窗仓位网格 + 仓位状态机（到位计时→放行→三色灯）+
  工艺策略窗（时长三级覆盖/光栅门控）+ 配置自愈，Phase 3 剩单线过账与
  工单/权限页

详见 `AGENTS.md`（开工必读）、`docs/重构设计_20261028.md`（两程序拆分设计）
与 `CHANGELOG.md`。

## 快速开始

```powershell
# 1. 拉代码（含子模块）
git clone https://github.com/lq700212/PLC_ReadData.git
cd PLC_ReadData
git submodule update --init

# 2. 编译子模块（net472，需 VS MSBuild）
& "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
  "extern/kaleidoscope/Kaleidoscope/Kaleidoscope.csproj" `
  /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m

# 3. 全量构建 + 测试
dotnet build PlcMesBridge.slnx --nologo
dotnet test tests/PlcMesBridge.Tests/PlcMesBridge.Tests.csproj --nologo

# 4. 模拟联调（无真机；两程序共用同名键 config.ini [setting] Simulate=1）：
#    - PlcMesBridge：复制 config.sample.ini 到其 bin 的 config/config.ini，
#      设 Simulate=1（多机网关直起即模拟 8 机台跑全流程）
#    - CureStation：程序①自己的 bin 下 config/config.ini 设 Simulate=1，
#      启动即 Mock 总线按仓位流转演示（30 卡片 + 周期演示掉线重连）
```

## 架构（MVVM）

- View（`src/PlcMesBridge/*.xaml` 纯绑定）← ViewModel（`ViewModels/`：
  状态 + 命令，`ViewModelBase` 管通知与跨线程封送）→ Model/Core
  （业务，可测）+ `IDialogService`（VM 开窗/提示唯一出口）。
- xaml.cs 只做三件事：设 DataContext、PasswordBox 密码转交（不可绑定）、
  转发关窗；VM 禁止碰 Window/MessageBox/Dispatcher；后台节拍经
  `UiInvoke` 回 UI 线程。
- VM 基建（`ViewModelBase`/`RelayCommand`）已下沉
  `src/PlcMesBridge.Core/ViewModels/`（namespace 不变，两程序共用）；
  `IDialogService` 各程序自建（窗集合是私有资产，不下沉）。

## 程序① CureStation（固化程控）

- **主窗**：30 张仓位卡（10 架×3 层，跟随点表扩展），每卡实时显示
  托盘/左右光栅/三色灯/仓位状态（状态机口径：空闲/待门控/计时中 剩X分/放行）；
  顶栏连接状态 + 当前策略 + 过账状态 + 重连 + 工艺策略/过账配置/点表配置入口；
  500ms 拉快照刷新。
- **登录与四级权限（P3.2）**：顶栏"登录"进四级操作员（L1 操作员/L2 班长/
  L3 工程师/L4 管理员，对照参考 HMI）；主窗功能入口按级别门禁——工单 L1+、
  工艺策略 L3+、点表/权限管理 L4+，未登录一律拦下。账号存 operators 表
  （PBKDF2 哈希），初始 admin/123456 上线必改；防锁死：最后一个 L4 不可删/降。
- **工单信息窗（P3.2）**：R/L 双工位（途程单号/预设数量/已生产数量 + 计数
  清零）+ 员工工号 + 物料条码 + 制程代码×2 + 物料上传使能；窗内编辑区再按
  级别细分禁用（途程/数量 L2+、制程 L3+）；存 `config/workorder.json`
  （字段名与 MES 16 字段对齐，过账直接映射）。**物料上传（P3.2 后半）**：
  批次物料区"上传 R/L 物料"两按钮——先保存工单再后台提交 API0032（刚录
  条码即时生效），结果看主窗顶栏过账状态。
- **单线过账（P3.2 后半，复刻老项目"装载/卸载"命令字）**：托盘到位=进站
  （API0030 I），托盘离开=固化时间（API0027）+出站（API0030 O），出站被
  MES 接受（STATUS=1）→ 对应工位"已生产数量"自动 +1。**全配置化**：
  总开关/工位/固化时间单位/进站时机/中途取件口径/静态参数/三接口 URL 全在
  **过账配置窗（L4）**改，保存即生效（热更新不重启）；总开关关或 URL 空
  不发任何请求；失败只记日志与顶栏状态（不弹窗不重传，防重复过账）。
- **仓位状态机（程控核心）**：托盘到位 →（门控开则等光栅让空）→ 黄灯计时 →
  到点绿灯放行 → 托盘离开灭灯回空闲；任何时刻托盘离开都回空闲（计时中取件
  记日志）。灯写/计时全在后台线程，断连恢复自动对齐重计，灯写失败下轮补写。
  **断电续算可配置**：策略勾"断电续算"后进行中仓位随状态变化落盘
  `config/curingstate.json`，重启按记录续算（断电时间计入固化时长）；缺省关
  =断电重计（宁可重计也不放行没固化够的货）。
- **工艺策略窗（参考配方管理模式）**：策略库增删选 + 编辑（策略名/全局时长/
  光栅门控/每层覆盖/每仓位覆盖行表）+ **激活并生效**（保存+启用选中策略）+
  **仅保存**（先校验后落盘，错弹框不写文件）。时长三级覆盖：仓位 > 层 > 全局，
  缺值自动回退，三种粒度可混用；光栅门控可开关（开=左右光栅都灭才开计时）。
- **点表配置窗（"点点鼠标"核心，现场不改代码）**：设备增删（IP/端口）+
  **一键缺省模板**（按架数×层数生成全套绑定）+ 绑定行增删改（块/位/方向/
  角色/架/层/功能名）+ **测试连接**（三态文字诊断：连不上/连上读不通/OK）+
  **保存**（先全量校验再落盘：物理点唯一 + 语义键唯一两道闸，错弹框不写文件）。
- **自愈**：`config/pointtable.json` 缺失/损坏 → 自动按 Audi cover module
  IO 表生成缺省模板（10 架 3 层）并落盘；`config/curingpolicy.json` 缺失/损坏
  → 缺省策略库（"标准固化"120 分钟）落盘，开箱即看。
- **点表布局（缺省，与电气 IO 表对应）**：块 K=架 K，块 K 的
  DI=0x1000+(K-1)×0x20、DO=DI+0x10；块内每 4 位一组=1 层——输入组
  bit0=左光栅/bit1=右光栅/bit2=托盘，输出组 bit0=红/bit1=黄/bit2=绿；
  起始地址/步进/取反三键均可配，代码零改。

## 界面设置

- 菜单栏"登录"按钮 → 登录窗（两档账号：admin 普通管理，
  dev 最高权限；"记住密码"按账号各记各的 DPAPI 密文，用户名框改谁就跟出谁的）。
  已登录再点可退出或切换账号；设置子项未登录会先提示登录。
  账号存数据库 users 表（PBKDF2 哈希，无明文），上线前必须改掉初始密码。
- 菜单栏"设置"下拉：admin 见"修改密码" + "创建桌面快捷方式"，
  dev 另见"PLC设置"。"修改密码"自己改自己（dev 可下拉代改他人），
  新密码≥6 位。
  "PLC设置" → PLC 配置窗（两页：多机PLC / 接口地址）：IP/端口/接口地址
  全界面改，"测试连接"拿表单值直测（不用先保存），"保存并重启"先校验
  格式、错当场拦，通过后自动重启生效。
  去现场只点鼠标，不改代码不改 ini。
- "创建桌面快捷方式"：一键在桌面建 `.lnk`（图标取 exe 内嵌图标，
  已存在不覆盖只提示路径）。exe/任务栏/全部 8 个窗体左上角图标统一为
  `src/PlcMesBridge/Assets/app.ico`，换图标只换该文件重新编译。
- 风格：天蓝色小清新全局样式（`src/PlcMesBridge/Styles/FreshBlue.xaml`）。

## 目录

- `src/PlcMesBridge.Core/` — 共享业务与基础（INI/SQLite/MES/通讯/网关逻辑 +
  **IO 点表家族** `Comms/IoPointTable*`/`IoBus`/连通测试 + 真机底座
  `Comms/ModbusStationClient`（Modbus TCP，地址/打包/分帧集中点）+
  **固化工艺家族** `Curing/CuringPolicy*`/`SlotCuringMachine`（策略三级覆盖 +
  仓位状态机）+ `ViewModels/` 下沉的 VM 基建）
- `src/CureStation/` — **程序①固化程控**（30 仓位主窗 + IO 点表配置窗 +
  工艺策略窗 + 工单信息窗 + 权限管理窗 + 登录窗 + 过账配置窗 + 仓位状态机
  + 单线过账服务）
- `src/PlcMesBridge/` — 主程序 WPF 界面（主窗/调试/报警/日志/图片窗），
  图标在 `Assets/`（`app.ico` 程序图标 / `app.png` 高清源备用）
- `tests/PlcMesBridge.Tests/` — 223 例 xUnit（`Mocks/`：本地桩 MES /
  网关标准报文 / 静态全局快照恢复 / Fake 对话框两套 / ScriptedPointBus 状态机桩，
  加用例直接复用）
- `extern/kaleidoscope/` — 通讯子模块（独立仓库，勿直接改，见 AGENTS.md）
- `docs/重构设计_20261028.md` — 两程序拆分设计与 Phase 路线
- `config.sample.ini` — 现场配置样例
