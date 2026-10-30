# CHANGELOG — CureLine（原 PlcMesBridge，V0.0.24 更名）

> VB 老项目复刻融合版，两程序拆分演进中。版本规则：关键行为变化即记一笔。

## V0.0.43（2026-10-31，策略画布傻瓜式直改：点格子直接设时长 + 特殊格蓝色识别）

用户拍板"操作尽量简单化，输入框太多显复杂，点方块直接在方块上改"——
仓位时长画布从只读预览升级为**主编辑区**：

- **点格子直接改（浮层输入卡）**：左键单击格子→浮层在选区旁浮出
  （标题"架X-层Y · 当前 N 分"，输入框预聚焦），打数字回车/【应用】即
  写入并关不掉（保持选中可连续改）；框选多格→同一张浮层（标题
  "架X-层Y、架Z-层W 等 N 格"）批量应用；Ctrl+单击=加选/减选；左键拖框=
  框选（橡皮筋，Ctrl 叠加并集）；点空白/Esc=清选择。浮层定位=选区包围盒
  （host 坐标系），缩放/平移/复位后重定位；焦点经 `Dispatcher.BeginInvoke`
  延迟一拍（鼠标 up 处理器内同步 Focus 被 WPF 点击聚焦覆盖的坑）。
- **交互键位重排（缩放平移全保留）**：左键从"平移"让给"点选/框选"，
  平移改由**中键拖动**独立承担（左键+中键共用的旧 `_dragButton` 逻辑收敛）；
  滚轮缩放（鼠标锚点 0.4~4.0）、双击**空白**复位（格子上双击不复位——
  不打断点选）、右键适应图像不变。图例栏操作说明同步重写。
- **特殊格蓝色自动识别（用户补需求）**：`SlotPreviewItem.IsSpecial`=
  草稿 `SlotMinutes` 覆盖字典**存在性真识别**（非"时长与层/全局不同"的
  数值推断——覆盖值恰好等于层/全局时仍算特殊，删覆盖才回普通），
  画布上特殊格=主题蓝 `Brush.Fresh.Accent`（字色 `OnAccent`），普通格
  保持时长四档色；图例加"特殊仓位（单独设过）"条目；ToolTip 带
  "特殊仓位"标注。选中态=黑/白描边（`FgStrong`，底色不变，与特殊蓝并存）。
- **界面降噪**：每层覆盖 4 框收进折叠 Expander（整层同时长推荐画布拖选
  整行批量设置——写仓位覆盖与层覆盖解析等效）；每仓位覆盖表降级为
  核对/精修/删行入口（提示"主入口=下方画布点格子直接设"）；画布行
  250→320、窗口 780→840。
- **数据链路**：选择真源=`_selectedKeys` 键集合（格子对象每次
  RebuildPreview 全新重建，选中态按键恢复）；浮层应用=逐格写
  `SlotRows` 覆盖行（有则改无则增，行匹配宽松化防手填空格）→ 行通知
  自动重建预览（数字/四档色/特殊蓝/右表全联动）；预置策略走同一
  编辑区草稿（套用为自定义时落库，语义不变）。
- **验证**：构建 0 警 0 错；测试 **391/391 全绿**（新增 6：单选应用/
  批量应用+更新已有行/非法输入拦+空输入静默/Toggle 与清空/重建恢复
  选中/IsSpecial 真识别）；UIA 真机目检四截图：单击 3-2 应用 45→蓝格
  +浮层标题实时"当前 45 分"+右表同步；框选 2-2~5-2 批量 90→四格蓝
  +右表 4 行；框选描边浮层批量卡正常；残留进程锁 exe 的构建坑已记
  （脚本开头兜底清理）。

## V0.0.42（2026-10-31，两窗目检修：点表布局重排 + 过账工位按需显示）

用户目检两轮反馈的界面修复（均真机截图验证）：

- **过账配置窗"过账工位"按需显示**：单工位（缺省）现场只有一套工位，
  常显 R/L 选择器会让人误以为要配双工位（用户拍板）——改为**勾选
  "双工位模式"才出现**（与同窗"双架工位映射/L 专用参数"同款 BoolToVis
  显隐）。**隐藏不等于停用**：`EditSide` 照常 Sync/Flush，ini
  `[CuringMes] Side`（缺省 R）原样往返，`SideOfRack` 单工位恒回它，
  语义零变化；口径说明同步（"按固定工位上报…勾开后才出现『过账工位』
  选择与 R/L 架号映射"）。
- **点表配置窗布局重排（V0.0.41 加批量生成后左栏内容爆栏）**：
  ① 窗口 1120×780 → **1400×880**（Min 1200×720，可缩不放破版）；
  ② 左栏下半部（设备名/IP 编辑、设备高级参数、批量生成、一键缺省模板）
  包进 **ScrollViewer**——旧版设备编辑区是 DockPanel 填充区、两个模板区
  钉在 Bottom，高度不足时**填充区被整体挤出可视区**（用户截图里设备名/IP
  与高级参数完全消失）；左栏顺序改为"选设备→改名/调参→批量生成→单台
  模板"动线；③ 中栏绑定表去钉死 `Height=420`，改 DockPanel 末位**占满
  剩余高度**——18 行绑定一屏看全（用户核心诉求"表格显示完整"），
  `HeadersVisibility=Column` 去行头省宽；④ 右栏 270 → 380（旧版测试结果
  5 列固定宽 300 > 可用 250，"结果"列被裁剩半字），消息列悬停展开全文
  （顶栏"短词+悬停全文"同款口径）。
- **验证**：构建 0 警 0 错；测试 385/385 全绿；UIA 真机目检三截图
  （点表新布局 / 过账单工位无工位行 / 勾双工位后出现且映射·L 参数同显），
  勾选未保存、config.ini 无写盘。

## V0.0.41（2026-10-31，多耦合器现场适配 + 全设备连接热更）

现场实况落定：一台机台 = **10 列 × 上中下 3 层 = 30 仓位，每列 1 台町洋
（Dinkle）iO-GRID NEMO GFNB-1A3A**（16DI+16DO，Modbus TCP，iO-GRID 家族
寄存器型访问与通讯库已接入的 GX-CL140 同款——架构层本就原生支持多设备，
本次全部补在"配置体验 + 热更"层）。**耦合器 IP 现场已配置：192.168.1.20~.29，
软件直接预置现值，现场零改 IP**。

- **批量一键生成（`IoPointTableStore.BuildMachineTemplate`）**：台数/起始架号/
  每架层数/IP 首址+步长 → 循环复用单台模板合成 N 台（每台 rackCount=1、
  GFNB 占位地址 DI 0x1000 / DO 0x2000、**TimeoutMs=300**——多台串行建连
  3s/台会把首轮拖到 30s，LAN 同网段 300ms 快失败快重连，配置窗可改）；
  IP 末段递增按四字节整数算（跨段进位也正确），多台步长 0 当场抛。
  点表配置窗左栏新增【批量生成机台耦合器】区（缺省参数=现场现值，
  确认弹框后覆盖重建设备列表）。**占位地址已对照官方手册核实**（iO-GRID
  NEMO GFNB Modbus TCP Operation Manual 6.4/6.5 节）：1A3A 的 DI 输入
  寄存器@0x1000（FC04）、DO 保持寄存器@0x2000（FC03 读/FC06 写），与
  缺省占位一致；另有位区 FC02@0x0000/FC05@0x0000 可用但无需。
- **自愈缺省模板改现场口径**：`MainViewModel.LoadOrDefault` 空表自愈由
  "1 台 10 架"改 `BuildMachineTemplate()`——10 台 × 1 架 × 3 层、IP .20~.29，
  现场首次开机落盘即是现值。
- **测试连接补 UI（原死命令）**：VM 的 `TestConnectionCommand` 与
  `IoCouplerConnectionTester` 一直都在但 XAML 没绑按钮——补【测试连接】
  （当前选中设备）+【测试全部】（逐台后台跑，结果行"时间/设备/IP/OK·NG/
  消息"进列表，最近 50 条，测试期间两钮齐灰 `IsTesting`）。测的是编辑区
  草稿值（先测再存）。
- **校验器补两道闸（`PointTableValidator`）**：跨设备**重复 IP:端口**拦
  （"两台不能连同一地址"）、**设备重名**拦（错误提示用 Name 定位，重名
  分不清台）。
- **轮询按块去重（`IoBus.cs`）**：`ScanPoints` 由"逐绑定各读 1 帧"改
  "按 (方向,块) 分组共用一帧"（纯函数 `ScanPlanner.BuildScanPlan` 可测）——
  10 台 × 每台 1 块 × 18 绑定从 **180 帧/250ms 降到 20 帧**；帧序稳定
  （块号升序、同块输入在前）。`WritePoint` 的 RMW 路径不动。
- **通道归属可见性**：点表窗中栏顶部"当前设备：名称（IP:端口）"提示随
  左栏选中联动；IO 测试窗每路通道悬停 Tip 带"设备：名（IP）"——10 台
  排障一眼定位耦合器。
- **程序②机台连接热更（本次热更专项唯一缺口）**：原"保存并重启"改
  **保存即生效**——`MainWindowViewModel.RestartConnections()`（后台
  Dispose 旧 `MultiMachineManager` → `InitMulti` 重建（factory 读新
  `AppConfig`）→ 复用 `ConnectMulti` 后台重连），配置窗保存返回后调用；
  文案"保存并重启"→"保存并生效"。**程序化重启链路整体删除**
  （`RestartApp`/`IsRestarting`/接口方法/Fake 桩/语言 key——按"改干净
  改彻底"），程序①耦合器点表保存→`StartBus` 整换总线本就是热更，
  不动。至此两程序所有连接/参数配置保存即热生效。
- **语言表**：新增"保存并生效/重连中.../连接已按新配置重建（保存即生效）"
  等 key，删除"保存并重启/已保存，重启软件后生效"等无消费旧 key。
- **验证**：全量构建 0 警 0 错；测试 **385/385 全绿**（新增 11：批量模板
  布局/自定义参数/零步长拦截/重复 IP/重名/同 IP 不同端口放行/读取计划
  单台与现场口径/批量生成 VM/取消确认/测试全部——真机自愈用例随
  TimeoutMs=300 由 34s 回落 7s，全量 17s）；Mock 冒烟：CureStation 删
  pointtable.json 重启自愈落盘 10 台现值，CimGateway 进程存活。

## V0.0.40（2026-10-30，程序②登录窗账号/密码输入左缘对齐修复）

用户目检反馈：程序② CimGateway 登录弹窗里 "admin" 首字符比密码圆点靠右
约 14px（V0.0.31 的 8:3 校准值出错）。像素级冒烟（UIA 点登录 → PrintWindow
截帧 + 逐像素扫描）定位根因：**自定义模板 ScrollViewer 已 Margin=Padding，
控件内部又消费一次 Padding——文本左缘 = 框内白 + 2×左Padding + 控件基础
内缩（TextBox 系 7px / PasswordBox 3px）**；2×8+7 与 2×3+3 差 14，与目测吻合。

- **校准（App.xaml FreshPwdInput + LoginWindow.xaml 注释）**：密码框左
  Padding 3→**10**（2×10+3 = 2×8+7，两框首字符左缘同线，冒烟实测两框
  墨迹 x=126 = x=126）；用户名 TextBox 保持左 8 不动。
- **程序①不动**：其"FreshPwdInput 左 3"是对 ComboBox（左 17）目检定标
  的另一套口径，两程序 UI 资源各自私有，勿互相套值。
- **验证**：全量构建 0 警 0 错 + 测试 374/374 全绿；冒烟截图像素扫描
  两框首字符 x 坐标同线。

## V0.0.39（2026-10-30，两主窗顶栏精简：低频按钮收拢"设置 ▾"下拉）

背景：两程序主窗顶栏按钮一排拉满（程序①原 12 钮/程序②原 7 钮），现场
屏幕宽度紧张、杂讯多。用户拍板"最精简"档：高频钮留顶栏，其余收进一个
"设置 ▾"下拉，**VM 层零改动**（命令/门禁/显隐/动态文案绑定原样搬移）。

- **程序① CureStation 顶栏**：只留 登录/工单/【日志】+【设置 ▾】；
  重新连接/演示开关/权限管理/工艺策略/过账配置/IO 通讯测试/点表配置/
  【深色】收进下拉（演示项用 `SettingsMenuBtnAccent`，开时强调色高亮）。
  **退出钮删除**——右侧系统 ✕ 走同一条退出链（code-behind `OnExitClick`）。
- **程序② CimGateway 顶栏**：只留 连接设备/【日志】/登录+【设置 ▾】；
  调试/网关设置（仅 dev 可见，`GatewayVisible`）/修改密码/快捷方式
  （登录启用，`SettingsItemEnabled`）/English/【深色】收进下拉。
- **实现（两 MainWindow.xaml + xaml.cs，各程序样式私有不下沉）**：
  `ToggleButton("SettingsToggleBtn")`+`Popup`（`StaysOpen=False` 点外自关、
  `AllowsTransparency`、`IsOpen`↔`IsChecked` 双向绑定）；弹层卡片走主题键
  `Brush.CardBg`/`Brush.CardBorder` + `Fx.WinShadow`（右/下边距留阴影区）；
  菜单图标复用既有 `Geo.*`，App.xaml 零改动；触发钮 `Content="设置"` 纯
  字符串（UIA/读屏 Name 由此生成）；菜单项点击统一 code-behind
  `OnMenuClick` 复位 `IsChecked` 收合。
- **冒烟验证（UIA 脚本，临时已删）**：PrintWindow 拍主窗 + 展开下拉后
  CopyFromScreen 拍弹层，两程序截图目检通过（程序①首拍曾出现重复"日志"
  钮——Popup 节后残留一份重复按钮，已删净重拍复验）；构建 0 警 0 错 +
  测试 374/374 全绿。
- **文档**：README 两程序界面描述同步（顶栏按钮清单/主题切换入口/IO
  测试与快捷方式入口措辞）；AGENTS.md 主窗导航行同步。

## V0.0.38（2026-10-30，双工位开关：现场单工位一步适配）

背景：现场确认只有一套工位（无 R/L 双工位），工单窗双面板与"上传 R/L
物料"双按钮、架号映射等对现场是噪音。取开关方案——**缺省单工位**，
真有双架分开记账的线再勾开，代码零分支部署。

- **新增配置键 `[CuringMes] DualSide`（缺省 0=单工位）**（`CuringMesOptions`）：
  - 关（缺省）：`SideOfRack` 恒回 `Side`（RackSides 即使残留配置也忽略，
    防误配把唯一一套工单引到空 L 槽）；进/出站与物料上传全按 Side 报本侧
    参数（`Mac*_L` 不生效）。
  - 开：老双架口径原样（RackSides 架号映射 + L 工位专用机台参数）。
- **工单信息窗单/双工位布局（`WorkOrderViewModel` + `WorkOrderWindow.xaml`）**：
  构造接 `postOptions`（生产装配 `DialogService.ShowWorkOrder` 现读
  `[CuringMes]`）。**视图↔槽恒定映射不动**（R 视图=Right、L 视图=Left，
  Sync/Flush 零改动——隐藏侧数据原样往返=盘上等值落回）：单工位只显
  `Side` 对应那套面板（`Side=L` 时主面板用 Left 槽，报文口径与界面一致），
  窗标题/面板标题/清零钮/上传钮文案全去 R/L 字样（"工单信息""计数清零"
  "物料上传"）；双工位=老 R/L 双面板口径不变。可见性走窗内
  `BoolToVis` 转换器 + `FallbackValue`（与 IoTestWindow 同款，设计时纪律）。
- **过账配置窗（`MesPostConfigWindow`）**："过账开关与口径"卡新增
  **双工位模式**勾选（L4 门禁内，保存即热生效）；双工位专属区块（架号
  映射、`MAC_NO_L`/`MAC_LOC_NO_L`）在开关关（单工位）时整块隐藏；口径
  说明补单/双工位一条。
- **过账顶栏文本随开关（`CuringMesPoster`）**：单工位不带工位字样
  （"物料上传 OK""进站 OK …"），双工位保持"进站[R]""R工位物料上传"
  等原样（报文瘦身仅文本层，接口地址/报文字段零变化）。
- **`config.sample.ini`** 同步键与注释（RackSides/Mac*_L 标注"仅双工位
  模式生效"）；README 工单窗/双架/过账三段同步。
- **测试 +6（全量 374/374 绿）**：`CuringMesPosterTests` 三例（DualSide ini 往返
  + 单工位 `SideOfRack` 忽略 RackSides + 单工位出站端到端报 Side 侧）、
  `CureStationViewModelTests` 三例（双工位双面板带工位字样 + 单工位 R 侧
  单面板无工位字样且 L 槽原样落回 + 单工位 L 侧主面板=Left 槽数据）。

## V0.0.37（2026-10-30，四窗目检优化：顶栏抓重点 / 画布中键拖动+右键适应图像 / 角色下拉中文 / 过账窗防重叠）

背景：用户连发四条目检——① 主窗顶栏左上"策略+设备+连接超时详情"文字
堆到显示不完整，要求抓重点；② 工艺策略窗预览画布要"按住滚轮拖动"+
"右键菜单适应图像（恢复打开时大小）"；③ IO 点表配置窗角色下拉显示
英文名（TrayDetect），要求像左表"角色"列一样显示中文，代码存储仍用
英文；④ 过账配置窗内容太挤、多处重叠。

- **主窗顶栏抓重点（`MainViewModel` + `MainWindow.xaml`）**：连接状态
  改两段口径——`ConnectionText`=短词（`DeriveConnectionShort` 纯函数
  派生：起停/配置/演示三类态原样；连通="已连接"；部分台在线
  （`1/2 台已连`收尾）保台数进度；全断或任意错误串="未连接"，与灯点
  红/绿同拍）；`ConnectionDetailText`=详情全文（总线摘要或设备错误串，
  如"A线OP30固化架IO耦合器 192.168.1.20:502 连接超时（3000ms），请检查
  IP/端口"）——**只走悬停 ToolTip，错误长文永远不进栏面**。顶栏布局
  顺改：dot+连接短词（Col0，点与词相邻语义直观）/策略（Col1）/（* 空列
  弹性撑开）/过账/操作员/按钮组（Auto 连排各补 18px 左间距防粘连，
  DocShot 首帧实拍抓出后修）。日志"系统状态卡"的"总线"行换用详情全文
  （快照定格要完整信息，短词留给栏面）。被推翻的旧口径"顶栏连接文本
  不截断"同步作废（AGENTS.md 已改写）。
- **策略窗画布交互补两项（`CuringPolicyWindow`）**：① 中键（按住滚轮）
  拖动平移=第二平移入口（左键保留，同一平移路径；只接管中键+已在拖动
  时忽略新键防双键互覆；拖动键记 `_dragButton`，只有放下"当前拖的那个
  键"才结束拖动）；② 右键菜单【适应图像】=显示恢复至窗口打开时的原始
  大小（与双击复位共用 `ResetView`）；图例侧栏说明补"左键 / 中键拖动"
  与"右键：适应图像（还原大小）"两条。
- **点表窗角色下拉显示中文（`PointTableViewModel.RoleOptions`）**：
  选项列表由 `IoPointRole[]` 改为（Value=英文枚举，Text=`IoPointRoleInfo.
  ToText` 中文文案）包装对 record `RoleOption`——与左侧表格"角色"列
  **同一来源永远同口径**；XAML `DisplayMemberPath="Text"` +
  `SelectedValuePath="Value"` → `EditRole`——落盘 JSON/校验/业务键全链
  仍是英文枚举（只改显示不改存储，用户口径原话）。
- **过账配置窗防重叠（`MesPostConfigWindow.xaml`）**："过账工位""进站
  上传时机"两组与静态参数 6 项由两列式 UniformGrid 改**纵排**（标签一行
  +控件一行，与"固化时间单位"V0.0.28 同因同治）——两列各 ~157px 装不下
  长标签（`进站上传时机（API0030 I）：` 截尾、`机台位置号-L工位
  (MAC_LOC_NO_L)：` 与右邻输入框挤同排），220 宽下拉还溢出到右列形成
  视觉重叠；窗体 720×560 → 880×800（首屏基本放全，仅底两项轻滚）。
- **测试 +2**（CureStationViewModelTests）：`PointTableVm_RoleOptions_
  TextIsChineseButValueStaysEnum`（选项数=枚举数、Text=ToText 同口径、
  Value=枚举原序）+ `MainVm_DeriveConnectionShort_CoversKeyStates`
  （六态：起停/配置/演示/连通/部分台/错误串——含"请检查 IP/端口"借
  '/' 误判台数进度的回归锚）。基线 366→368。
- **DocShot 四帧换代**（cs-main-idle 顶栏 / cs-policy 画布说明 /
  cs-pointtable 角色下拉 / cs-mespost 纵排重拍，其余 cs-*/gw-* 为
  时序刷新属正常入库）。

## V0.0.36（2026-10-30，IO 两窗目检答疑：测试窗圆点形状说明 + 点表窗选中行联动修复）

背景：用户目检两问——① IO 通讯测试窗仓位卡里"小圆是什么、大圆是什么"
（猜小圆=DI、大圆=DO，猜对了但界面没说）；② IO 点表配置窗右侧"绑定编辑"
面板不随表格选中行变化，疑似 bug（实锤：`PointBindingRow.RefreshDisplay`
注释写着"选中行变更→编辑面板回显"，意图存在而 `SelectedBinding` setter
没接线，编辑面板永远停在换设备时的首行）。

- **IO 测试窗圆点形状说明（`IoTestWindow.xaml`）**：顶栏说明改写为
  "小圆=DI 输入（只读灯）；大圆=DO 输出（可点切换 ON/OFF）——与用户
  猜测一致（卡内上排 15px 小圆 Ellipse=Inputs 只读、下排 24px 大圆
  Button=Outputs 可点击）；详细形状/颜色/悬停释义全文放 ToolTip（顶栏
  宽度有限，正文保精简）。
- **悬停提示方向首标（`IoTestPointVm.Tip`）**：每个圆点悬停第一段改为
  "DI 输入·角色 / DO 输出·角色"——与顶栏"小圆=DI/大圆=DO"呼应，
  悬停即可确认通道方向，无需回看顶栏。
- **点表窗选中行联动修复（`PointTableViewModel.cs`）**：`SelectedBinding`
  setter 联动 `SyncEditorFromSelected()`（值没变不回显，不打断正编辑的
  草稿）；随改删三处显式重复调用（`ReloadBindings`/`DeleteBinding` 的
  手动 Sync 与 `GenerateTemplate` 的重复 `ReloadBindings`——setter 已
  覆盖，双份口径容易日后改一半漏一半）。
- **DocShot 点表帧联动演示（工具链）**：`cs-pointtable.png` 改拍"选中
  位号 2 托盘检测行"状态，表格选行与右侧编辑面板同屏展示"选中谁就
  编辑谁"（手编点表无 X2 输入行时回落首行兜底，帧照拍）。
- **测试 +1**：`PointTableVm_SelectedBindingChange_ReechoesEditor` 锁
  联动链（生成模板自动回显首行 + 选 X2 行编辑面板七字段全同步）。
  基线 365→366。

## V0.0.35（2026-10-30，主窗仓位卡设计图一比一复刻：胶囊+大数字+进度条，头带/灯点退役）

背景：用户出最新设计图（B02 卡=白底黄描边+右上"计时中"胶囊+中部"47 分"大
数字+副文"共 120 分 · 已固化 61%"+进度条+底部状态点），拍板主窗 30 张仓位卡
一比一复刻（结构/字号/风格全对齐）；V0.0.26 的"头带+灯点"口径随之退役。

- **卡片模板重写（`MainWindow.xaml` SlotCard）**：top=库位码 17px ExtraBold
  （空闲/未配置灰降权）+ 右上六态状态胶囊（Curing/Gate/Done/Alarm/未配置=
  `Brush.State*` 彩底白字，Idle=浅灰底灰字，顶替灯点）；mid=状态词 15px Bold
  +副文 10.5px（Gate"光栅让空后计时"/Done"待取件"/Alarm"红灯 · 请检查"）
  或大数字 26px ExtraBold"47 分"+副文 Run"共 120 分 · 已固化 61%"+ 6px 圆角
  进度条（Grid 槽+Rectangle ScaleTransform.ScaleX 直绑 `ProgressRatio`，
  轻量不走 ProgressBar 控件）；foot=7px 状态彩点（随 Kind 六态）+层名+
  托盘（"◈ 托盘在"深色半粗，不再绿强调）；5px 头带 Border 与 Ellipse 灯点
  整件删除；非空闲卡状态描边加厚 1.5（触发器 `BorderThickness`）；Alarm
  呼吸脉冲动画转挂 mid 状态词（原口径保留）。
- **VM 同源进度比（`MainViewModel.cs`）**：`RackSlotViewModel.ProgressRatio`
  （double 0~1）与 `ProgressPct` 同在 `ApplyKind` 一处计算（不变量：
  TotalMin=0 → Pct/Ratio 双零）——进度条 ScaleTransform 与副文百分比同源同拍。
- **主题键板同步（Light/Dark 同名异值，改干净改彻底）**：删退役键
  `Brush.Head*` 六键（头带）+ `Fx.DotGlow*` 五键（灯点发光，唯一消费者
  已删除）；新增 `Brush.PillIdleBg`（胶囊空闲灰底）；Light 改值`TintIdle`→
  #F5F7FA 浅灰空闲卡、`SlotBorderGate`→#B39DDB 紫描边、`NumCuring`→#F59E0B
  设计图橙大数字；Dark 同删同加，`NumCuring` 保持霓虹浅白（G1 无设计图，
  原拍板不动）只补说明注释。
- **DocShot harness 修复升级（工具链）**：① `SilentGatewayDialogs` 补
  V0.0.32 改名后的 `ShowGatewayConfig` 桩 + alias 去 PLC 化
  （`GwPlcConfig`→`GwGatewayConfig`）；② 程序②资源装载重做——原
  `Styles/FreshBlue.xaml` 已随 V0.0.20 删除（pack 404 → gw 段 StaticResource
  全爆），改为 `run_docshot.ps1` 从两程序 App.xaml 摘段生成
  `Cure/Gw.App.Resources.xaml` + 从程序集 pack 装主题键板，gw 段截图恢复；
  ③ 新增高进度演示帧 `cs-main-curing.png`——Mock 演示计时卡恒"刚开跑"口径
  （剩 119/进度 1%）拍不到续算填充，harness 反射停 500ms 刷→直改 rack1/L1
  为"剩 47/共 120=61%"（设计图 B02 数）→拍帧→复跑刷新（统计条同拍重算，
  下一拍恢复真实状态零污染）。
- 测试：新增 `Kind_ZeroTotal_RatioAndPctClampedZero`（双零不变量），
  `Kind_Curing_CarriesRemainAndProgress` 补 ProgressRatio 断言；全量
  365/365 绿，0 警告 0 错误。

## V0.0.34（2026-10-30，IO 测试窗多机台下拉切换——一次 30 库位不再挤压）

背景：双架（及以上）点表在 IO 通讯测试窗默认平铺全部架号的卡（RackCount=20 时
UniformGrid 一行 20 列），60 张卡挤一行每张窄如竖条，通道看不清——用户拍板
"参考主窗机台分域思路，一次只测一台机台，每行固定 10 库位"。

- **机台下拉框（程序① IO 测试窗顶栏）**：双架及以上点表时顶栏连接灯旁新增
  【机台选择】下拉（FreshCombo 同款样式），选项文案="N号固化架"（全局架号每
  10 站一台，`SlotLocationCode.RackGroupOf` 同源分组）——切换即重建显示卡，
  **一次只显示当前机台的 30 个库位**（每行固定 10 个，与主窗行优先契约一致）。
  单机台（仅 1 台）**隐藏下拉框**，视觉与旧版完全一致。
- **选项只列"有绑定"的机台段**：rackStart=11 的单机台点表只出"2号固化架"一项
  （不会冒出空荡荡的"1号固化架"占选项）；一个绑定都没有（空表）按 1 号机台
  兜底铺占位卡。
- **卡标题换库位编码口径（对齐主窗）**：`架11-层1` → `A01-上层`——
  `SlotLocationCode.EncodeInRack` 架内站号换算，每台架各自一套 A01~J12，
  与现场库位标签对照；切换机台后编码从 A01 重新起算，同源不歧义。
- **内部口径**：全量卡按全局架号先建齐（`_allSlots`），`Slots` 收窄为"当前
  机台的 30 张"（切换零重建，只换引用集合）；`_points` 全量点位链不变——
  读取状态/全部关闭/一键遍历照旧**全域 DO 生效**，不随当前选中机台收窄；
  XAML 行宽绑定改吃 `Columns`（=RacksPerRow），`RackCount` 不再当显示列数。
- 测试：`IoTestVm_BuildsSlotsFromPointTable` 换新口径断言 + 新增
  `IoTestVm_MachinePicker_SwitchesSlots`（切换重建/架号段/库位编码/行优先/
  切换日志 5 项锁定）；全量 364/364 绿，0 警告 0 错误。

## V0.0.33（2026-10-30，六份培训手册随 V0.0.31/32 改版同步：新图 + 新界面口径）

背景：V0.0.31（程序②双主题/无边框/按钮组）与 V0.0.32（去 PLC 命名 + 机台名
配置化）改变了界面与配置口径，原培训手册的截图与描述已过期。

- **截图全面重拍（18 张，docs/images/）**：程序① 12 张（主窗浅色/深色/双主题
  对照、登录窗、改密面板、工单窗、权限窗、策略窗、过账配置窗、IO 测试窗、
  点表窗、日志悬浮窗）；程序② 6 张（主窗、登录窗、日志窗、调试窗、改密窗
  admin 态、网关配置窗 dev 态）。全部走"演示模式 + 真实状态机"实拍
  （Mock 演示数据驱动，非 P 图）；改密窗改用 **admin 登录态**拍摄（顶栏无
  "网关设置"钮），供客户两版使用。
- **六份手册更新**：`固化程控_操作员/客户技术工艺/内部手册.md` +
  `整线网关_操作员/客户技术工艺/内部手册.md`——
  ① 图片引用全部换新图（旧名 cs-main-idle/cs-main-flow/cs-workorder-l1/l3/
  gw-plcconfig 等已废）；
  ② 程序② 顶栏描述由"菜单 设置▼"改为按钮组口径（连接设备/日志/调试/修改
  密码/快捷方式/English/登录/深色）；程序① 补【演示：开·关】与【深色·浅色】
  按钮说明（操作员版明确"产线不许开演示"）；
  ③ 程序② 内部手册配置键名中性化（`StationUse`/`StationIp1..8`/`StationPorts`/
  `API00xxStationDataAddr`/`CureLine.db3`/`cure_record`）+ 机台名 `[StationNames]`
  配置化说明；客户版补"机台名/主题可配置"两段；
  ④ 权限红线复查：客户/操作员四份零出现"网关配置/网关设置/dev/开发者面板"
  （check_docs 禁词门禁全绿）。
- **核验工具更新**：`tools/DocShot/check_config.py` 常量表换新图集
  （IMG_WHITE 18 张、DEV_ONLY_IMGS 收敛为 gw-config.png、REQUIRED_IMGS 按新图
  重排）；`check_docs.py --pdf` ALL PASS（6 份 md + 6 份 PDF >50KB 配对）。

## V0.0.32（2026-10-30，机台名配置化 + 全项目去 PLC 命名中性化）

背景：审视扫描发现最后一个"换产线要改代码"的硬编码——8 台机台名写死在
`MesLogger.StationNames`；用户同时拍板"项目没有 PLC，不要考虑兼容老项目，
改就改干净彻底（数据库 db 文件可直接删）"。

- **机台名配置化（`[StationNames]` 节键 1..8）**：缺键/空值逐台回落缺省名
  （`AppConfig.DefaultStationNames`=老项目 Designer 原样，ini 自愈语义）；
  `MesLogger.StationNames` 改为委托 `AppConfig.StationNames`——机台面板名/
  主窗视图筛选下拉/MES 日志文件名三处同源；网关配置窗"机台连接"页每行新增
  **机台名输入框**（加载即回填当前生效名，改名保存重启生效）；校验拦"非空 +
  ≤50 字 + 无文件名非法字符"（机台名会当日志文件名用）。
- **全项目去 PLC 命名中性化（用户拍板：本项目与机台通讯，无 PLC）**：
  - 文件/类型：`IPlcClient`→`IStationClient`、`SimulatedPlcClient`→
    `SimulatedStationClient`、`PlcConnectionTester`→`StationConnectionTester`、
    `PlcConfig.cs`→`StationConfig.cs`（`PlcConfigStore`→`StationConfigStore`、
    `PlcConfigValidator`→`StationConfigValidator`、`StationPlcConfig`→`StationConfig`）、
    `PlcConfigViewModel`→`GatewayConfigViewModel`、`PlcConfigWindow.*`→
    `GatewayConfigWindow.*`（与界面标题"网关配置"对齐）、`ShowPlcConfig`→
    `ShowGatewayConfig`、`PlcVisible/PlcEnabled`→`GatewayVisible/GatewayEnabled`、
    `BtnPlcText`→`BtnGatewayText`、`PlcChanged`→`ConfigChanged`、
    `IsPlcAddress`→`IsStationAddress`、`ApiAddressSet.PlcData`→`StationData`、
    `AppConfig.PlcUse/PlcIps`→`StationUse/StationIps`。
  - ini 键名：`PLCuse`→`StationUse`、`PLCIP2..7,9,10` 跳号键→**`StationIp1..8`
    连续键**（"多机键名跳号勿整理"的历史包袱随之解除，用户拍板覆盖）、
    `PLCPorts`→`StationPorts`、`API00xxPLCDataAddr`→`API00xxStationDataAddr`。
  - 数据库：`PLCdatabase.db3`→`CureLine.db3`、表 `PLCtable`→`cure_record`
    （EnsureDatabase 自建，项目未上线无兼容包袱，现场删旧 db 即可）。
  - 文案/词条：界面错误文案"PLC地址格式须为D/R+数字"→"寄存器地址格式须为
    R/D+数字"；日志标题"PLC Trigger/PLC Read Data"→"Station Trigger/Read Data"；
    LanguageService 删除无消费方的 6 条老词条（"连接PLC/PLC心跳/PLC地址:
    /手动从PLC读取/单机PLC/连接PLC时发生异常"——单机链路遗产），带消费方的
    4 条改中性键并同步英译；注释按语境改"机台/寄存器"。
  - **保留例外**（非本项目命名）：kaleidoscope 子模块外部 API
    （`PlcMasterConfig`/`PlcAddress`/`MultiBrandPlcConfig`——子模块零改动红线）；
    密码学字面量（PBKDF2 Pepper 与 DPAPI 附加熵含旧项目名 "PlcMesBridge"，
    改动会使既有哈希/密文全部失效）；VB 老项目控件/函数专名
    （`PLC_ReadData`/`ScanPLC1`/`LabelPLClive`——复刻溯源标识）。
- **测试**：新增 6 例（机台名缺省回填/保存往返/AppConfig 带出且 MesLogger
  同源/名字校验/配置窗改名保存落盘/非法名拦保存）；适配键名与词条改动的
  既有断言；357→363 全绿。模拟模式 GUI 冒烟通过（配置窗机台名框回填、
  面板/下拉同源）。

## V0.0.31（2026-10-30，程序② CimGateway UI 对齐程序①：无边框自绘 + 双主题 + 全弹窗换肤）

背景：用户要求程序②界面与 CureStation 风格保持一致，深浅双主题都要有，
各弹窗界面全部优化。架构与程序①同构不同文件（主题键板是程序私有资产，
分机部署独立演进，与"对话框接口不下沉"同精神）。

- **主题体系移植（`src/CimGateway/Themes/`）**：Light.xaml（G2 浅色渐变浸染）/
  Dark.xaml（G1 深空玻璃舱）语义键同名异值字典 + `ThemeManager` 切换中枢
  （ParseTheme 纯函数非法回 Light + 测试态防御），App.OnStartup 按
  `[setting] Theme` 应用（与程序①同名键，各自 exe 的 ini 独立生效）。
  字典只保留本程序有消费者的键（无仓位卡/统计胶囊/工单卡——无消费者不放键）；
  `Styles/FreshBlue.xaml`（单一浅色键板）废止删除，全站取色改走主题语义键。
- **App.xaml 键板重建**：GhostButton/PrimaryGhostButton（顶栏胶囊钮）+
  Fresh 弹窗样式族（次级/主/危险钮 + FreshInput/FreshPwdInput/FreshCombo +
  ComboBoxItem）+ 隐式 CheckBox/ListBox/ToolTip/胶囊滚动条 + WinTitleBar
  三样式（弹窗自绘标题条）+ WinSysBtn 系统钮；**新增 TabControl/TabItem
  主题样式**（配置窗两页：选中页签=卡底与内容框连体，未选=透明纯文字）；
  Geo.* 图标库按需精简（新增 Geo.Link 快捷方式图标）；Label 缺省字色走
  Brush.FgMid（原系统黑字深色下不可读）。
- **主窗无边框自绘**（对齐程序① V0.0.28 口径）：OS 标题栏移除，品牌顶栏
  即标题条（CaptionHeight=44：chip"CIM"+标题+时钟 / 功能按钮组 / 系统三钮
  ─ □·❐ ✕，最大化 RootGrid 垫回 resize 边框防裁切）；原 WPF Menu 菜单栏
  拆为顶栏按钮组（"设置"下拉拆成 网关设置/修改密码/快捷方式 三枚独立钮，
  门禁原样：网关设置 dev 可见、修改密码/快捷方式登录后可用）；新增
  【深色/浅色】主题钮（切换即写 ini + VM 记操作日志）与 BtnLoginText 短文案
  （按钮上只显示"登录"/账号，原 LoginHeader 保留给测试与提示文案）。
- **StationPanel 机台卡换肤**：白卡/玻璃卡随主题（CardBg/CardBorder 三键），
  心跳灯由硬编码色名（Lime/Red）改 DataTrigger 映射主题状态键
  （StateGreen/StateRed，缺省 StateIdle）——深浅主题灯色自动跟随。
- **全部弹窗统一圆角自绘骨架**（WindowStyle=None + WindowChrome +
  Fx.WinShadow + WinTitleBar 品牌标题条 + 自绘 ✕）：登录窗（对齐程序①
  登录窗布局：FreshInput/FreshPwdInput 左缘对齐契约 + AlertErr 提示条 +
  主蓝确定钮）、改密窗（FreshCombo 用户下拉 + 三密码框）、网关配置窗
  （Tab 主题化 + FreshInput 表单 + 测试结果字色改语义名触发器——VM
  ResultForeground 由 "DarkGreen"/"Red" 改 "Ok"/"Err" 映射 AlertOkFg/
  AlertErrFg）、调试窗（FreshPrimaryButton 上传 + 只读框复用 FreshInput
  框体）、日志窗（危险清空钮 FreshDangerButton）、报警窗、图片查看窗。
  可缩放弹窗（调试/日志/报警/图片）ResizeBorderThickness=6。
- **测试**：新增 `CimGatewayThemeTests` 12 例（ParseTheme 纯函数 + 两字典
  x:Key 集合全等锁 + Fresh 弹窗键板/主窗键板全集在两字典齐备——键漂移
  挡在最便宜的位置）；345→357 全绿。

## V0.0.30（2026-10-30，顶栏按钮紧凑化 + 演示模式开关 + 登录窗密码对齐）

背景：用户目检指出顶栏功能按钮高度撑满整条 44px 顶栏（"都顶到顶了"）；
随后要求把 Mock 演示模式做成主窗开关按钮；又指出登录窗密码首字符与
账号框文本左缘不对齐。

- **顶栏按钮"顶到顶"修复**：功能按钮组与 Height=44 的系统三钮（最小化/
  最大化/关闭）同处一个水平 StackPanel——水平 StackPanel 子元素垂直方向
  默认 Stretch，系统钮把整条撑到 44 高后所有 GhostButton 被跟着拉满
  （V0.0.28 无边框化引入）。`GhostButton` 加 `VerticalAlignment=Center`
  根治 + Padding `10,4`→`13,5` 对齐设计稿 `.btn` 尺寸（约 27px 高）。
- **演示模式开关（Mock 数据热切换，L4 门禁）**：顶栏"重新连接"后新增
  【演示：开/关】按钮——写回 `[setting] Simulate` 并立即经 StartBus 重建
  总线（Mock↔真机免重启，状态机 Attach 换通道流程不丢）；开启态高亮
  （PrimaryGhost 三键 DataTrigger）一眼区分；L4 门禁（演示边沿会照常驱动
  状态机与过账链，影响生产数据口径）。ini 只是开机初值，按钮即改即生效。
- **登录窗密码首字符对齐**：`FreshPwdInput` Padding `8,0`→`3,0,8,0`——
  PasswordBox 内部渲染无 TextBoxView 的额外内缩，同抄 FreshInput 的 6
  反而比用户名编辑框文本（模板 Margin 8）靠右约 3 逻辑 px；实机像素
  定标 3（两框首字符左缘同列，"账号/密码文本左缘对齐" UI 契约）。
- **总线停机竞态根治（测试长跑暴露）**：两总线 `StopCore` 原 `Cancel` 后
  立即 `Dispose` CTS 且不等线程——在飞 `Task.Delay(token)` 摸到已释放
  CTS 冒脏 Error、连接线程卡超等待上限时残留事件晚到，把新总线的顶栏
  连接文本覆盖成旧总线报错（热切换/重连偶发）。三道闸：① StopCore 先
  竖 `_stopped` 静默阀再 Cancel，RaiseError 拦截 + `Wait(2000)` 等线程退出
  后才放资源；② VM 侧三事件回调统一比对 `sender==_bus`，旧总线事件
  无论多晚一律忽略；③ 测试收集容器换 `ConcurrentQueue`（Mock 演示线程
  并发 Add 与断言 enumerate 竞态）。修复后 30 轮压测全绿（修复前失败率
  约 13%）。

## V0.0.29（2026-10-30，深色模式黑字清零 + 弹窗圆角化 + 主窗顶栏对齐 G1 对比稿）

背景：现场目检发现深色模式下各功能弹窗大量裸 TextBlock（字段标签/图例）
未设前景色，落 WPF 系统默认黑字在深空底上不可读；另用户指出主窗顶栏结构与
`docs/UI设计_固化架显示区优化_对比稿.html`（G1）对不上、要求全部弹窗圆角化。

- **深色黑字清零（根治法）**：9 个窗体根节点统一设 `Foreground` 兜底
  （7 个 Fresh 弹窗=`Brush.Fresh.FgMid`、主窗/日志窗=`Brush.FgMid`）——
  未显式着色的裸文本沿树继承主题亮色，Light=#333333 深字浅底、
  Dark=#9DB8DC 亮字深底两主题自动正确；已显式着色/样式着色的文字不受影响
  （Control.Foreground 元数据默认值截断继承的裸 Control 本项目无裸用，
  输入框/下拉/按钮/表格全走主题化样式）。不采用隐式 `TextBlock` 样式——
  样式 Setter 会截断"容器着色→子继承"链（按钮亮字/表格选中亮字会被压回）。
- **登录窗下拉项回归主题**：用户名下拉 `ItemContainerStyle` 原无 BasedOn，
  整体覆盖回系统默认模板（深色下弹层白底刺眼）——补
  `BasedOn={StaticResource {x:Type ComboBoxItem}}`，只留 19px 左缘对齐补丁。
- **弹窗圆角化（8 窗统一骨架）**：`AllowsTransparency=True + Background=
  Transparent` + 外层 `CornerRadius=10` 圆角 Border（底色 `Fresh.WinBg`/
  `WindowBg`）+ 新增 `Fx.WinShadow` 浮层投影（Blur 24/Depth 2，窗根
  Grid Margin=14 留渲染白）；`WinTitleBar` 共享样式与日志窗 `LfHead`
  补顶部 `CornerRadius=10,10,0,0` 贴弧。新弹窗照抄该骨架即可。
- **主窗顶栏对齐 G1 对比稿**：删品牌 chip"固"（对比稿无此元素）；左侧=
  连接灯点+策略+连接状态（对齐稿）；过账/操作员两段状态文本（功能超集，
  稿中无）移到按钮组前弱化显示；按钮相对顺序对齐稿（登录/工单/权限/策略
  在前、【日志】高亮居末段、退出收尾——原"退出"夹在中间）；过账配置/
  IO测试/点表/重新连接/深色为稿后新增真实功能按钮保留。
- **按钮图标化（galaxy 本地资源沉淀，2026-10-29 用户拍板落地）**：新增
  `Geo.*` 图标几何库（App.xaml，17 键）——全部取自 `E:\Project\galaxy`
  （uiverse-io/galaxy 本地镜像）按钮样例内嵌 SVG 的 path（24×24 线性
  风格，来源文件见各键注释），转译 XAML Geometry 原样搬 d；`GhostButton/
  FreshButton` 模板加图标位（`Tag` 挂 `Geo.*` 即显示，`Stroke` 绑定
  `TemplateBinding Foreground` 自动跟随主题前景/悬停色，无 Tag 折叠向后
  兼容）；落点=主窗顶栏 10 钮（深色钮语义即文案不加）+ 各弹窗高频操作
  （保存/激活/删除/添加/读取/修改密码等约 20 处）。资源来源约定写入
  AGENTS.md"UI 资源来源"节：各类 UI 图形资源先翻 galaxy 本地库、缺失才
  上游获取，配色一律换主题语义键。

## V0.0.28（2026-10-29，主窗 OS 标题栏自绘 + 过账配置窗"固化时间单位"截断修复）

背景：V0.0.26 主窗仍是 OS 系统标题栏（顶部 30px 白条与品牌顶栏割裂），
V0.0.27 已把 8 个弹窗统一无边框自绘，主窗作为最后一块补齐；另过账配置窗
"固化时间单位"下拉文案尾部截断（V0.0.26 既有列宽口径）一并修。

- **主窗无边框自绘**：`WindowStyle=None + WindowChrome`（CaptionHeight=44
  对齐品牌顶栏，整条命中拖动 + 双击最大化还原；ResizeBorderThickness=6
  还原态可拉伸；GlassFrameThickness=0 关 DWM 玻璃边）。顶栏功能按钮组末补
  **系统三钮**（最小化 ─/最大化还原 □↔❐/关闭 ✕，新增 `WinSysBtn/
  WinSysClose` 两通用样式——关闭与"退出"按钮同链 Dispose+Shutdown，
  还原图标随 WindowState 切换）；顶栏 `GhostButton` 样式级声明
  `IsHitTestVisibleInChrome`（caption 区内所有按钮可点）；最大化时根 Grid
  按 `SystemParameters.WindowResizeBorderThickness`（已含 DPI 修正）垫回
  边距，防顶栏/工单卡被屏幕边裁切（`ApplyRootMargin`，Loaded+StateChanged
  双锚）。Light/Dark 同构生效。
- **过账配置窗"固化时间单位"行重排**：定宽 220 与 Auto 均被 UniformGrid
  两列式列宽（≈164px）钳住，最宽选项"Seconds（秒，老项目口径）"（自然宽
  ≈206px）尾部截断；该行拆为"标签一行 + 下拉一行"独占整卡宽，Auto 自然
  舒展完整显示（"进站上传时机"行保持两列式不动）。Light 侧该窗布局随之
  微调（用户批准，其余界面 Light 零变化）。
- **DocShot 双主题重拍**：cs-main-idle/flow/flow2/cs-mespost 四张两套均重
  拍归档后仅保留受影响四张入库；Dark 全套像素白区扫描 cs_ 12/12 OK
  （主窗 OS 白条消失，不再计入白区）；gw_* 属程序②浅色窗口固有白底控件
  形态，不在主题跟随范围，与本轮无关。
- 测试 0 改动：全量 342/342 全绿；build 0 警 0 错。

## V0.0.27（2026-10-29，弹窗跟主题第 2 期：Fresh 键板两字典化 + 8 窗无边框自绘 + ListBox 容器换肤）

背景：V0.0.26 主窗双主题完成后，8 个功能弹窗仍是硬编码浅色一套，与 Dark
主窗配色断层。本轮把"弹窗维持浅色"的旧口径**整体废止**：弹窗与主窗同用
Light/Dark 两套主题字典，切主题（V0.0.21 起的顶栏按钮）全程序一动全换皮。

- **Fresh 键板两字典化**：`Brush.Fresh.*` 54 键（输入框底/边框/前景三阶/
  强调/按钮三态/卡片/胜利红/警示橙/点表网格等）收进 `Themes/Light.xaml`
  与 `Themes/Dark.xaml` **同名异值**；`App.xaml` 删除全部固定色键（grep
  0 残留），FreshButton/FreshInput/FreshCombo/CheckBox/DataGrid 等样式
  Setter 全改 `DynamicResource`——弹窗浅色值一字未动（Light 零回归），
  Dark 出一套 G1 深空玻璃同族口径（WinBg=#0F1B30/CardBg=#12203A/
  InputBg=#0F1D33/ButtonHoverBg=#22345A 等）。
- **8 窗无边框自绘统一**：登录/工单/策略/IO 测试/点表/权限/过账配置/日志
  窗全部 `WindowStyle=None` + `WindowChrome`（标题条命中拖动 Caption 34/
  36，ResizeBorder 6 可拉伸，登录窗 0）+ `WinTitleBar/WinTitleText/
  WinTitleClose` 三通用样式；各窗 xaml.cs 加 OnCloseClick。
- **通用控件主题化（App.xaml 隐式样式）**：ComboBoxItem/CheckBox/
  Expander/ToolTip/ListBoxItem/ScrollBar/DataGrid 行列选中 + FreshCombo
  自绘下拉模板（PART_Popup 主题弹层）；**ListBox 容器本体也上隐式样式**
  （缺省 Aero2 呈白底大块——"选中行白底"的表象根因是整框没换肤，策略库/
  设备列表/日志窗一并覆盖）。
- **DocShot 双主题截图 harness**：`DOCSHOT_THEME=Dark/Light` 环境变量
  注入（与 seed ini 主题同源）；run_docshot.ps1 从 App.xaml 摘生成的
  字典剥壳 + 移除内层 MergedDictionaries（相对 Source 在 DocShot 组件
  解析不到），DocShot.cs 直接 pack 加载 CureStation 编译好的主题字典。
  两主题各 20 张全 ALL PASS，Dark 像素级白区扫描除主窗系统标题栏外 0 白。
- **测试 +2**：`ThemeDictionaryTests`（两字典 `x:Key` 集合全等锁键不漂移
  + Fresh 全集两字典齐）；全量 342/342 基线只增不减。

## V0.0.26（2026-10-29，主窗 G2/G1 对比稿一比一复刻）

背景：V0.0.25 方案 D 的双主题是"意译色板"（键都有但视觉与设计稿对不上）。
本轮按 `docs/UI设计_固化架显示区优化_对比稿.html` **一比一复刻**：Light=
G2 浅色渐变浸染 / Dark=G1 深空玻璃舱，两主题截图对照 HTML 逐项迭代贴版。
纯展示层改动，业务逻辑零改动（文案锚点 `SlotStateText`/`NextCloseText` 格式
不动——有测试断言锁死）。

- **主窗全量重写**（`MainWindow.xaml`，对齐 HTML `.g2-*/.g1-*` 直译）：
  - 顶栏合一单条（全宽贴边 44px）：Light=品牌蓝渐变 / Dark=#101A2C 深蓝实底。
    左=chip"固"+连接灯点+策略+连接（* 弹性列完整显示）+过账+操作员；右=功能
    按钮组（登录/工单/权限/策略/过账配置/IO测试/点表/重新连接/退出/深色/
    【日志】高亮 PrimaryGhost）。命令与门禁与旧两段顶栏完全一致；
    GhostButton 圆角 14→4（HTML .btn 直译）。
  - 仓位卡换 **G 卡三段式模板**（makeG 直译）：top=库位码+灯点 / mid=垂直居中
    大数字（17px + "分·%"小字）或状态词 / foot=层名+托盘。**卡内无进度条**
    （HTML G 卡无 bar，V0.0.25 的 4px 卡内进度条删除）；idle/gate 灯点=灰不发光
    （HTML makeG：dot 只在 lampOn 才发光）；Done="✓ 放行"、Alarm="⚠ 占用"
    用触发器字面量（不绑 VM）；Alarm 脉动从整卡改挂 mid 文本（Opacity
    0.7↔1，1.5s）；G1 光斑（CardGlow*）与状态头带同为 Kind 触发器；卡投影
    Fx.CardShadow（Alarm 切红光圈 Fx.CardShadowAlarm）。
  - 统计胶囊行：StatPill 通用样式（18 圆角/padding 11,4），文案对齐 HTML 口径
    （空闲/计时中/待门控/**待取件**（原"放行"）/**占用报警**（原"红灯"））+
    ⚡剩余最短虚线框胶囊（Rectangle StrokeDashArray="3 2" 直译）。
  - 工单双卡（HTML g2-wo/g1-wo 直译）：R/L 各"工位 · 单号"+过账角标
    （PostingEnabled DataTrigger→"过账启用"绿 / 否则灰底"过账未启用"）+
    6px 渐变进度条（R 蓝/L 紫；G1 加霓虹 glow `Fx.WoBarGlowR/L`，Light 同名键
    x:Null=无光）+"已产出 N / 总"（新增 `WoQtySplitConverter` 拆分，VM 零改——
    `SummarizeWorkOrder` 输出格式被测试断言锁死不动）。
- **LogWindow 标题条**换 LfHead 系列（HTML lfhead 直译：渐变头+绿点+白字；
  底边共用 TopbarBorder 键）。
- **VM 三项小改**（`MainViewModel.cs`，废弃直接删·铁律 9）：删 CardState/
  TrayText；加 TrayFoot（"◈ 托盘在"/"无托盘"/"—"）、LayerName（上层/中层/
  下层）、PostingEnabled（工单卡角标，与 PostingText 同两处赋值点）。
- **字典死键清除**：删 PanelGrad/PanelBg2（两字典）与 PanelBorder2（两字典）；
  PanelBg/PanelBorder 仍被 LogWindow 使用保留。
- **测试 +2 例（340 全绿，基线 338+2）**：TrayFoot 三态/LayerName 映射、
  WoQtySplitConverter 拆分与兜底。
- **已知取舍**（对比稿有但 WPF 贴不齐，主动放弃）：空闲"空 闲"字距
  letter-spacing:3px 无 WPF 等价物，用字面空格占位；G1 卡 text-shadow 光晕
  （XAML 文本投影成本高、增益小）放弃。
- **冒烟踩坑沉淀**：PowerShell 5.1 `Set-Content -Encoding UTF8` 写 UTF-8 带
  BOM，kernel32 读 ini 时 BOM 破坏首节名 → `[setting] Theme/Simulate` 全落
  缺省（改 ini 必须无 BOM 写，`[System.IO.File]::WriteAllText` + UTF8Encoding
  ($false)）。
- **验证**：`dotnet build` 0 警告 0 错误；`dotnet test` 340/340 全绿；
  GUI 冒烟双主题截图对照（Light=G2 / Dark=G1 均贴版，mock 点表自愈 60 卡）。

## V0.0.25（2026-10-29，程序①主窗方案 D 重画 + 双主题 + 日志悬浮窗）

背景：程序①主窗沿用骨架期灰色网格，信息密度与观感落后于拍摄设备实景。
本轮按 docs/UI设计_固化架显示区优化_20261029.md 拍板的**方案 D（紧凑网格
+ 下部信息带）**全量重画主窗，并落地**双主题切换**与**日志/控制台悬浮窗**。

- **方案 D 五段布局**（`MainWindow.xaml` 全量重画）：品牌条（品牌蓝渐变）/
  运行条（连接状态 + 过账 + 操作按钮）/ 统计条（五态 pill + ⚡剩余最短）/
  固化架分组（双架纵向堆两组，单架视觉不变）/ 工单带（R/L 双工位纪要，
  LastWriteTime 变了才重读工单簿，500ms 同拍不反复碰盘）。卡片换新模板：
  顶部 5px 状态渐变头带 + 状态浸染底/描边 + 六态 Kind（Idle/Gate/Curing/
  Done/Alarm/NotConfigured，`RackSlotViewModel` 新增，老 CardState 保留），
  计时中显示剩余分钟大数字 + 4px 进度条（`SlotProgressBar` 模板，
  PART_Track/PART_Indicator）；脉冲动画仅挂 Alarm 卡。行优先集合契约不动。
- **双主题**：`Themes/ThemeManager.cs`（ParseTheme 纯函数 + ApplyTheme 换
  MergedDictionaries[0]）+ `Themes/Light.xaml`（G2 浅色渐变浸染，缺省）/
  `Themes/Dark.xaml`（G1 深空玻璃舱）——两字典语义键同名异值（约 60 个
  `Brush.*`），主窗/日志悬浮窗全键化 DynamicResource 取色，窗体 XAML 零
  主题分支；`[setting] Theme` 落盘（缺省 Light、非法回 Light，主窗右上
  【深色/浅色】按钮切换即写回，App.OnStartup 按 ini 应用）。**其余功能弹窗
  维持浅色**（`Brush.Fresh.*` 固定键收 App.xaml 主字典，切主题不影响），
  弹窗随主题属第 2 期。
- **日志/控制台悬浮窗**：主窗日志带移出 → 顶栏【日志】按钮（L1+ 门禁）
  弹出非模态悬浮窗 `LogWindow`（右下角定位、Owner=主窗、`DialogService`
  静态单例防重复开）：左栏操作日志流（`LocalLogService.Written` → 无锁入队
  → 100ms 批量搬运，最新在上封顶 200 条），右栏系统状态卡（打开一刻定格
  快照，含当日日志目录路径）。锁内事件"轻量投递"红线以 ConcurrentQueue
  落地；关窗 Detach 退订防悬挂。
- **统计纯函数下沉 Core**（可测）：`SlotStatistics.Count`（五态计数）/
  `NextClose`（⚡剩余最短库位）；主窗工单带文案映射 `SummarizeWorkOrder`
  纯静态函数。测试 +23 例（338 全绿，基线 315 只增不减）。
- **crash 修复（关键）**：`ProgressBar.Value` 默认双向绑定，绑到 VM 只读
  属性（`ProgressPct`/`WorkOrderPctR`/`WorkOrderPctL` private set）在
  500ms 刷新首次给值时抛异常——现场表现为启动后报"无法对只读属性
  ProgressPct 进行 TwoWay 绑定"错误弹窗（无人点框时 fail-fast 0xC0000405，
  事件日志伴生 TextShaping.dll 栈溢出假象）。三处绑定补 `Mode=OneWay`。
  教训沉淀：**给只读 VM 属性绑 RangeBase 类控件（ProgressBar/Slider/
  ComboBox.SelectedItem）必须显式 Mode=OneWay**。
- **配置样例同步**：`config.sample.ini` 新增 `[setting] Theme=Light`（铁律 8）。
- **验证**：`dotnet build` 0 警告 0 错误；`dotnet test` 338/338 全绿；
  GUI 冒烟双主题截图目检（Light/Dark 各 30 卡与双架 60 卡两版）。

## V0.0.24（2026-10-29，项目更名——PlcMesBridge → CureLine）

背景：仓库/解决方案/共享库的历史名 PlcMesBridge 源自 VB 老项目 PLC_ReadData 的
"PLC 桥接 MES"定位。项目重心早已是**固化产线设备端软件套件**（程序①固化程控 +
程序②整线 CIM 网关），PLC/MES/Bridge 字样都不再准确概括。随远端仓库同名变更，
全仓库统一更名为 **CureLine**（固化产线；与程序① CureStation 同词根，
"站∈线"语义自洽，甩掉所有 PLC 残留）。

- **改名范围**（git mv 全程保留历史）：`PlcMesBridge.slnx` → `CureLine.slnx`、
  `src/PlcMesBridge.Core` → `src/CureLine.Core`（工程文件同步更名）、
  `tests/PlcMesBridge.Tests` → `tests/CureLine.Tests`（工程文件同步更名）；
  全部 namespace/引用/文档同步（`PlcMesBridge.Core.*` → `CureLine.Core.*`、
  `PlcMesBridge.ViewModels` → `CureLine.ViewModels`、`PlcMesBridge.Tests` →
  `CureLine.Tests`），共替换 119 个文件。
- **补上 V0.0.21 刻意不动的两样**（当时唯一悬留)：共享库/测试工程名与解决方案
  文件名本轮一并更名完毕，仓库内自助检索已无 PLC 字样的现役标识。
- **远端仓库**：`lq700212/PLC_ReadData` → `lq700212/CureLine`（GitHub 仓库改名
  后旧地址自动重定向；本地 remote url 已同步更新为新地址）。
- **现场无感**：exe 名不变（`CureStation.exe`/`CimGateway.exe`）、运行时落盘
  （config/DataBase/log）与仓库名无关，现场无需迁移任何数据或配置。
- **刻意不动的两处字面量**（继承并固化 V0.0.21 的盐值纪律）：
  `PasswordHasher.Pepper = "PlcMesBridge.v1::field-panel"`、
  `RememberMeProtector` 熵 `"PlcMesBridge.RememberMe.v1"`——它们是内部盐值/
  DPAPI 熵不是身份标识，改动=现网 users/operators 全部哈希复核失败（无人能登录）
  + "记住密码"已存密文失效；两文件注释均已写明缘由，后续维护者勿手痒。
- **历史严格不动**：CHANGELOG 历史条目、各处"原名 PlcMesBridge"括号注、
  docs 施工文中的历史称谓保持原样（git 考古与现场旧文档对照不受影响）。
- **验证**：`dotnet build` 0 警告 0 错误，`dotnet test` 315/315 全绿
  （基线只增不减）。

## V0.0.23（2026-10-29，培训文档套装 6 份——两程序全量交付件）

背景：交付清单缺客户可读的三档文档（操作员/客户技术工艺/内部）。
本次全流程跑通培训文档生成：盘点权限口径 → 截图 harness 实拍 →
分段写作 → 核验全绿 → PDF 转换，沉淀成两程序各三份共 **6 份 md + 6 份 PDF**。

- **6 份文档**（`docs/`，PDF 在 `docs/pdf/`）：
  程序①固化程控（操作员 L1 视角 / 客户技术工艺 L3 视角 / 内部全量）+
  程序②整线网关（操作员 / 客户工艺 / 内部全量）。
  **权限可达铁律**：程序② dev 专属页（网关设置/网关配置窗）在其中
  4 份客户向文档**零出现**（正文禁词双保险 + 截图 `gw-plcconfig.png`
  仅内部手册引用——`tools/DocShot/check_docs.py` 断言锁死）。
- **截图 20 张**（`docs/images/`，`cs_*`=程序① 12 张 + `gw_*`=程序② 8 张）：
  离屏 WPF 拍摄 harness（`tools/DocShot/`，PrintWindow 含标题栏 +
  RenderTargetBitmap 兜底 + 宽高/像素指标三道 R3 断言）——
  Mock 种子配置自动注入（演示数据注明）、关窗确认弹窗链路全程绕开
  （主窗 Hide+Environment.Exit 收尾）、演示掉线时段自动避开。
- **核验脚本**（`tools/DocShot/check_config.py` + `check_docs.py`）：
  6 份文档 H1/必含截图/白名单引用/客户向禁词/内部版全量图 5 道闸，
  `--pdf` 追加 PDF 存在性 + >50KB 防空渲染；**check_docs: ALL PASS (md+pdf)**。
- **PDF 转换脚本**（`tools/DocShot/md_to_pdf.py`）：markdown→HTML→
  Edge headless 打印（A4/微软雅黑/print-color-adjust 保色、中文文件名
  经 ASCII 临时名双保险、临时 HTML 转完即删）。
- **README**：目录节补培训文档条目 + 测试总数口径修正（223→315）。
- **测试零改动**：本轮只加 `tools/Docs*`（不进 slnx、不进 git 的
  bin/obj/seed/runner 跑完即删）；src 零触碰，315/315 保持全绿。

## V0.0.22（2026-10-29，整线网关参数全配置化——P2.2 联调预备）

背景：P2.2（6 接口迁移与整线联调）卡在等 D/R 终版地址 + CIM W68 终版寄存器
表。盘点发现 URL/触发字/6 组地址束/DataLength/字节序**早已 ini 可配 + 配置窗
可编辑 + 保存热生效**，真正残余硬编码只有两处——本次补齐，W68 终版到位后
**改配置即可，代码零改动**（用户拍板"做成动态可配置，通用性更强"）。

- **DATA 分段偏移配置化**：API0030/0031 返回 DATA 三段拆写（DATA/+30/+60）
  的偏移做成 `[MES] DataSegmentOffsets`（缺省 "30,60"=老项目口径，须 2~8 段、
  每段 1~65535；**任一非法整体回缺省并回写 ini**，不做部分生效防现场配一半）。
  `MesConfig.DataSegmentOffsets` + `MultiMachineGateway.WriteDataSegment`
  改读配置；`ShiftAddr` 算法本体不动（heritage 锚点）。
- **扫描周期配置化**：网关触发轮询间隔 200ms 硬编码 → `[setting]
  ScanIntervalMs`（钳制 50~5000，越界回写钳制值）；`StationGateway.Loop`
  每次 Sleep 读静态值——配置窗保存 `AppConfig.Load()` 后**即时热生效，
  无需重连**。
- **配置窗补两个输入**：接口页"数据长度"行旁加"分段偏移"（ToolTip 口径
  说明）；多机页"全部测试"行前加"扫描周期ms"。校验与 AppConfig.Load 同
  口径双保险（窗拦一道、Load 兜底自愈）。
- **刻意不做（过度设计防线）**：① 触发判定值（=1）不配——CIM 触发协议
  约定，配置化无意义；② 接口数量/schema 不配——6 接口报文模型是 CIM W68
  约定，真变了本来就要改代码（ValidateJson switch + MesModels）；③ 每机台
  独立地址束不配——8 台 PLC 布局相同是整线网关前提（老项目口径），真有
  差异再立项。
- **测试 +7（308→315）**：偏移/周期加载缺省自愈、合法照读、非法整体回缺省、
  网关自定义偏移拆写（旧偏移地址不写）、配置窗 VM 回显/保存热生效/非法拦；
  `TestScope` 静态快照纳入两个新静态（防用例间污染）。顺带清了上轮遗留的
  `IoTestViewModel.StopSweep` CS8602（全量重建才暴露的可空流分析告警）。
- **界面名去 PLC 化（V0.0.22 补丁，用户拍板）**：程序②已定名 CimGateway，
  界面框架名跟着定位走——主窗标题"PLC通信"→**"CIM通信网关"**、设置菜单
  "PLC设置"→**"网关设置"**、配置窗"PLC配置"→**"网关配置"**、页签
  "多机PLC"→**"机台连接"**、机台面板心跳 ToolTip→"通讯心跳"；英文词条
  同步（CIM Gateway/Gateway Settings/Gateway Config/Stations）；桌面快捷
  方式默认描述跟主窗标题。**刻意保留**：日志内文"连接PLC成功"等（描述的
  就是 PLC 连接行为，属实）与代码标识符（PlcConfig* 类型名）；LanguageService
  中的死词条（连接PLC/PLC心跳/单机PLC/运行模式等，单机链路遗留零调用）
  留待后续大扫除。
- **验证**：`dotnet build` 0 警告 0 错误；`dotnet test` 315/315 全绿；
  Simulate=1 冒烟见配置窗两新输入；主窗标题"CIM通信网关"截图目检通过。

## V0.0.21（2026-10-29，程序②改名 CimGateway 定名）

背景：拆分设计里程序②的定位就是 **CIM 整线过账网关**（Phase 2 目标命名
CimGateway），但目录/工程/exe 一直叫 PlcMesBridge，与"固化单线"和"整线总账"
的分工容易混淆——按用户拍板正式改名定名。**纯身份改名，业务零改动**。

- **改名范围**：目录 `src/PlcMesBridge` → `src/CimGateway`（git mv 保留历史）、
  工程文件 `CimGateway.csproj`（显式 `RootNamespace`/`AssemblyName`=CimGateway，
  exe 产出即 `CimGateway.exe`）、程序②内全部 namespace（`PlcMesBridge`/
  `.ViewModels`/`.Controls` → `CimGateway.*`）+ 10 个 XAML 的 `x:Class`/
  `clr-namespace`、解决方案条目与测试工程 `ProjectReference`。
- **刻意不动的三样（防误会也别改坏）**：① 共享库 `PlcMesBridge.Core` 工程
  与 namespace 整体不动（基建下沉既定约定，两程序 + 测试零感知）；②
  `PasswordHasher` 的 Pepper 与 `RememberMeProtector` 的熵串仍含 PlcMesBridge
  字样——**密码学派生常量，改动即现场已存密码哈希/记住密文全部失效**，
  它们是内部盐值不是身份标识；③ 解决方案文件名 `PlcMesBridge.slnx` 保留
  （对应仓库名 PLC_ReadData，是"整个仓库"的名字，不是程序②的名字）。
- **顺带**：`DesktopShortcut` 快捷方式兜底名 "PlcMesBridge" → "CimGateway"
  （与 exe 名对齐，测试断言同步）；文档口径统一（README/AGENTS/重构设计
  全部 `src/CimGateway` 路径 + 改名说明）。
- **验证**：`dotnet build` 0 警告 0 错误；`dotnet test` 308/308 全绿；
  Simulate=1 起 `CimGateway.exe` 冒烟（进程存活 + 主窗 8 机台面板正常 +
  DataBase 自建落盘）。

## V0.0.20（2026-10-28，双固化架 60 仓位 + 待定项全配置化 + 登录/权限窗美化）

背景：客户现场可能出现**一台工控机接两台固化架**（每台 10 站×3 层=30 仓位，
合计 60 卡），要求主窗同时显示、布局可配置兼容；同时用户要求把"未完成无法
确定"的现场校准项尽量全部做成傻瓜式配置（部署人员点点鼠标，几乎不改代码）。
顺带：登录窗参考 HJVision 登录页布局重做（配色仍按本项目 Fresh 风），权限
管理窗按钮语义色化。

- **双架主窗布局（上下分组）**：`MainViewModel` 新增 `RackGroups` 分组视图
  （`RackGroupViewModel`：标题"N号固化架"/组内行优先卡列表/列行数/标题条
  显隐），全局架号每 10 站切一组（1~10=1号架、11~20=2号架，与
  `SlotLocationCode.RacksPerRow` 同源）；`MainWindow.xaml` 主内容改绑分组
  ——外层 `UniformGrid Rows=GroupCount` 纵向均分铺满，每组带标题条 + 组内
  10×3 网格；**单架分组数=1 且标题条隐藏，视觉与旧版完全一致（零回归）**。
  平铺 `Slots` 集合与行优先契约原样保留（回归锚点不动），组内卡片是同一批
  实例（引用共享，500ms 一遍刷两组同步）。
- **客户库位编码双架口径**：`SlotLocationCode` 新增 `RackGroupOf`/
  `StationInRack`/`EncodeInRack`——库位标签（Date Matrix）贴在每台架自己的
  仓位上，每台架各自独立一套 A01~J12，全局架号按**架内站号**换算（架 11
  层 1 → A01），归属靠分组标题区分；`Encode`/`TryDecode` 本体（客户约定
  锚点）不动，单架两口径完全等价。卡片 `Code` 改走 `EncodeInRack`。
- **双架接入配置化（点表窗点点鼠标）**：① 模板生成加"起始架号"参数
  （`BuildDefaultTemplate` 缺省 1，第二台设备填 11 即生成架 11~20，设备内
  地址换算不受影响）；② 点表校验"语义唯一"提升为**跨设备全场查重**——两台
  耦合器撞架号保存前拦下（快照按语义键合并多台设备，撞号会同键互相覆盖）；
  ③ 点表窗新增"设备高级参数"折叠区（端口/从站号/DI·DO 起始地址/块跨度/每块
  点数/超时/轮询周期/输入·输出取反）——此前这些只能手编 pointtable.json，
  现场校准全 UI 化，地址文本支持 `0x` 十六进制与十进制两种写法。
- **双架过账工位映射**：`[CuringMes]` 新增 `RackSides`（"11-20:L" 架号段:
  工位，分号多段，非法段忽略；空=全用 Side，单架零感知）——自动进/出站按
  全局架号查映射定工位槽；静态参数新增 `MacNo_L`/`MacLocNo_L`（L 工位专用，
  留空回落主值）；物料上传不走映射（按钮工位红线不变）；过账配置窗加对应
  输入框。报文无架号字段（批次级过账口径不变），顶栏状态文本带架号可追溯。
- **字符串字节序配置化（程序②，W68 联调预备）**：`[setting] StringByteOrder`
  （BigEndian=老项目口径缺省 / SwapBytes=CIM 侧低字节在先）——
  `ModbusStationClient` 新增 `SwapStringBytes` 开关 + `SwapPairs` 纯函数，
  ReadString/WriteString 按开关字节对交换，现场校准零改代码。
- **登录窗重做**（`CureStation/LoginWindow.xaml`，布局参考 HJVision 登录页、
  配色按本项目 Fresh 蓝系）：无边框窗 + 蓝色品牌标题条（拖动/× 关闭走
  CancelCommand）+ 居中横幅 + 左标签右输入（新增 `FreshInput`/`FreshPwdInput`
  统一输入框样式：圆角 4、聚焦变主蓝边）+ 错误提示条（Tip 非空才显示）+
  底部按钮行（登录=新增 `FreshPrimaryButton` 实色蓝、取消=次级）；键盘链
  用户名回车→密码框、密码回车→登录、Esc→取消。**VM 与门禁链零改动**
  （PasswordChanged 转交/RequestClose 挂 DataContextChanged/Level·LoginName
  输出原样）。`FreshButton` 升级带圆角+悬停反馈模板（全局观感统一）。
  **补丁（2026-10-29）**：输入框模板裁字修复——PART_ContentHost ScrollViewer
  不得设 `VerticalAlignment=Center`（会收缩内容宿主、裁掉文字下半截），
  改 Stretch 填满 + 控件 `VerticalContentAlignment=Center` 居中，Height 加到 32。
  **功能对齐 mFormLogin（2026-10-29 补丁，参考 HJVision mFormLogin 功能清单）**：
  ① 登录窗内置**修改密码面板**（登录 ⇄ 改密双面板同窗切换，横幅随面板换文案）
  ——四道闸：账号在册（`OperatorStore.Exists` 新增）→ 原密码验对 → 规则
  （≥6 位 + 不得与旧相同，走 `PasswordHasher.ValidateNewPassword`）→ 两次一致，
  过了 `SetPassword` 写盘即生效，成功回登录面板 + 绿色成功提示条（错误红条/
  成功绿条随 `TipIsError` 切换）；② **记住密码 + 密码跟随账号**（复用主程序
  同款 `RunModeSettings` 记忆件：DPAPI 密文存 config.ini，ini 里永无明文）
  ——分账号各记各的，用户名改谁密码/勾选跟谁；登录成功落盘（勾记住存本户、
  没勾清本户），改密成功同步新密码到记忆；③ **用户名下拉=在册账号实时表**
  （`ListUsers`，比 mFormLogin 的登录历史文件干净：无已删账号残留零新存储）；
  ④ 键盘链对齐 mFormLogin 矩阵（View 层 PreviewKeyDown 统一处理，按钮不用
  IsDefault/IsCancel 防双面板吃错回车；下拉展开时回车/Esc 放行给 ComboBox）：
  登录面板用户名回车→密码框、密码回车=登录、Esc=关窗；改密面板三框回车依序
  跳框、确认回车=保存、Esc=回登录面板。冒烟走通全链：改密→新密码登录→
  记住回填（重开窗勾选自动勾上）→改回初始密码。
  **UI 微调（2026-10-29 二轮）**：① 登录窗删三处文本——副标题"四级操作员
  登录（LEVEL.1~4）"、级别说明行、标题条"操作员登录"文字（蓝条+× 保留，
  拖动/关闭不受影响）；② 用户名下拉编辑框/下拉列表项（`ItemContainerStyle`
  Padding 补 19px）/密码框三处文本左缘对齐（放大截图逐像素核对）；③ 主窗
  顶栏登录态改"**管理员：admin**"格式（`OperatorLevelInfo.ShortName` 级别
  短名开头，不带"操作员"前缀、不带 LEVEL 字样；级别全名只在权限管理窗展示）；
  ④ 顶栏连接状态文本去掉 40 字硬截（骨架期防挤残留，* 弹性列本不挤按钮），
  完整显示 + `TextTrimming` 极窄兜底 + ToolTip 悬停看全文。
  **工艺策略预置库 + 可视化画布 + IO 通讯测试窗（2026-10-29 二轮）**：
  ① **预置策略库**：`CuringPolicy.IsPreset` 出厂模板标记（老 JSON 无字段
  缺省 false=全按自定义，兼容零迁移）；缺省库 6 策略——"自定义"（激活）+
  5 个预置样例（标准 120/快速 60/深度 240 带门控/分层固化演示每层覆盖
  90·120·150/特殊仓位示例演示仓位覆盖 3-2=45·7-1=180）。预置策略可选中
  查看/编辑参数，但**保存/激活/删除一律拦下**（激活只认自定义——预置是
  起点模板保证可回溯），唯一出口=【套用为自定义策略】（编辑内容整体替换
  写入"自定义"并激活，对齐 AgingTestSystem 重名覆盖语义；先改副本：草稿
  进库校验通过才落选中对象，改名撞车不污染原策略）。开窗默认选中激活
  策略（新库=自定义）。② **仓位时长可视化画布**（策略窗底部）：30 格
  （架×层，与主窗网格同源，双架点表自动铺 60 格）按解析时长四档着色
  （绿<60/黄 60~119/橙 120~179/红≥180）+ 图例；**拖动平移 + 滚轮缩放
  （鼠标锚点，0.4~4.0）+ 双击复位**（View 层 RenderTransform，业务零
  耦合）；编辑全局/每层/仓位任一时长实时反映（数字错兜底照画）。
  ③ **IO 通讯测试窗**（顶栏"IO测试"入口，L3+；移植 AgingTestSystem
  通讯测试思路并通用化）：通道清单完全来自点表（界面可配——换项目/
  换耦合器零代码）；仓位卡网格与主窗同布局，DI 只读状态灯（500ms 快照
  刷新）+ DO 点击灯钮（Task.Run 后台写，真机走总线 RMW 不误伤同寄存器
  其它通道；写入中防抖；失败弹回真实读数不误导）；读取状态/全部关闭/
  **一键遍历**（500ms/路跑马灯逐路点亮验接线，运行中按钮变"停止遍历"，
  停止自动全关）；共享主窗 `IPointTableBus` 连接（不另建 TCP，事件桥接
  进日志，关窗 Detach 退订）。④ 全项目输入框/下拉框统一 32 高与按钮
  对齐（App.xaml 新增 `FreshCombo`，六窗 TextBox/ComboBox 批量套
  FreshInput/FreshCombo）。测试 287→308（+21：预置库/套用链/三拦截/
  先改副本防脏/预览解析与分档/老 JSON 兼容 13 + IO 测试铺格/快照三态/
  写点回显/失败弹回/防抖/全关/遍历启停/事件桥接与退订 8）。
- **权限管理窗语义色**：按钮编排参考用户管理页——新增=实色蓝、改级别=次级、
  重置密码=绿（`FreshSuccessButton`）、删除=红（`FreshDangerButton`），补
  ToolTip；布局与逻辑不动（明文密码框维持现状，改 PasswordBox 属单独任务）。
- 测试 248→287（+39：库位编码双架口径 13 + 双架分组/单架回归 3 + 跨设备
  查重与 rackStart 模板 3 + RackSides 解析与端到端 12 + SwapPairs 3 + 登录
  记忆/跟随/改密链 5，含各既有回归）；构建 0 警告 0 错误。

## V0.0.19（2026-10-28，主窗仓位布局对齐机台实景 + 客户库位编码）

背景：客户给定《自然静态养护(24H)库位编码表》（Date Matrix 格式：10 站一排
A~J × 上中下 3 层，编码 = 列字母 + 「列序+层序−1」两位数字，如 A01/J12），
主窗 30 张仓位卡要求与现场机台编排一致（三排十站），且后续有扫码绑定工序。

- Core 新增库位编码换算（`Curing/SlotLocationCode.cs`）：架×层 ↔ 库位编码
  互转（`Encode`/`TryDecode`），常量 10×3 即客户编码表口径；收在 Core 是为
  扫码绑定预留同源入口——界面显示与将来扫码绑定同一套换算，防口径分叉。
  越界（点表绑 >10 架 / >3 层）给占位码 `R{rack}L{level}`，不抛异常不猜码。
- 主窗布局重排（`CureStation/MainWindow.xaml` + `MainViewModel.BuildSlots`）：
  集合改**行优先**（先层后架：上层 10 站一排 → 中层 → 下层），UniformGrid
  Columns/Rows 绑 VM 新增 `RackCount`/`LevelCount`——三排十站与机台实景
  一致；卡片主标题改库位编码（架层辅助小字），新增状态底色（红/黄/绿/空闲
  四态 DataTrigger）与三色灯状态点；托盘/光栅信息保留（光栅弱化为小字）。
- 屏幕适配：默认最大化启动（`WindowState=Maximized`），卡片不设固定宽高、
  随窗口拉伸均分（大屏铺满/小屏压缩，`MinWidth=1200`/`MinHeight=600` 兜极
  小屏），卡片内容垂直居中。
- 测试 223→248（+25：`SlotLocationCodeTests` 24 例——编码表逐格对照/全表
  唯一/往返反解/非法拒收 + VM 布局契约 1 例——行优先顺序/RackCount/
  LevelCount/编码首末卡）；构建 0 警告 0 错误；GUI 冒烟通过（Simulate=1
  全屏截图：三排十站、编码 A01~J12、演示流转黄底计时卡与灯点正常）。

## V0.0.18（2026-10-28，P3.2 后半：单线过账全配置化）

背景：程序①固化程控接 MES 上报（复刻老项目 `PLC_ReadData` 单机固化收料的
"装载/卸载/资料获取"命令字链路）。**CIM W68 终版未定 → 全部不确定项做成
可配置 + 界面化**（用户口径：现场"点点鼠标，配置下就结束"，代码零改动）。

- Core 新增过账配置模型（`Curing/CuringMesOptions.cs`）：`config.ini [CuringMes]`
  节——总开关 Enabled（缺省 0=关）/工位 Side（R/L）/固化时间单位 TimeUnit
  （Seconds=老项目口径/Minutes）/进站时机 PostInAt（Arrive=托盘到位/Start=
  开计时）/中途取件口径 PostAbortOut（缺省 1=老项目 Case3 照常传出站）+
  静态参数五件（MAC_NO/MAC_LOC_NO/PLANT/PRA_CODE/CHECK_RESULT，留空回退
  `mes_params.json` 记忆值）；缺键回填缺省（IniFile 自愈同款）。
- Core 新增过账服务（`Curing/CuringMesPoster.cs`）：状态机边沿 → MES 报文。
  * **触发映射**（老项目命令字 → IO 边沿）：托盘到位=进站（API0030 ACTION=I，
    老项目"装载"）；托盘离开=出站链（先 API0027 固化时间——外层数组、
    PRA_Value 按单位配置，后 API0030 ACTION=O，老项目顺序）；出站被 MES
    接受（STATUS=1）→ 对应工位"已生产数量"+1 落盘（0027 失败只报警不挡
    累计——出站才是过账完成标志）。
  * **WaitingGate 离开补出站**：Arrive 口径下进站已发，固化 0 秒补出站
    （有进必有出）；Start 口径没发进站则出站也不发（自洽）。
  * **发送门槛双保险**：总开关开 且 接口 URL 非空 才发——现场没配 MES 不
    产生网络请求；失败只记 MES 日志 + 主窗顶栏状态文本（**不弹窗不重传**
    ——老项目写 PLC 报警位+弹窗，新项目点表只有三色灯，差异已登记）。
  * **线程模型**：四入口全 Task.Run 后台 + 最外层 try-catch 兜底（状态机在
    _sync 锁内调本类方法，方法体只投递不等待，绝不阻塞程控——铁律 2）。
  * **热更新**：`UpdateOptions` Volatile.Write 换引用——配置窗保存即生效，
    不重启不重建不重订阅。
  * **BC_NO=工单批次物料条码**（批次级过账）：IO 耦合器方案无字符串寄存器
    读产品 ID 通道，板级上报走程序②整线网关（架构差异已登记）。
- `SlotCuringMachine` 挂三事件（TrayArrived/CuringStarted/TrayLeft，带相位
  与已固化秒数）：**只发"真实边沿"**——重启对齐（AlignFromSnapshot）不发，
  防 MES 重复过账；事件在 _sync 锁内触发，**红线=处理器禁止同步回调状态机**
  （Poster 内部 Task.Run 立即返回）。
- CureStation 装配：App 启动 `AppConfig.Load()`（[MES] URL + 参数记忆）；
  主窗建 Poster + 订阅状态机事件（创建处一次，重连 Attach 不重复订阅）+
  顶栏过账状态文本 + "过账配置"按钮（L4 门禁，保存后热更新 + 顶栏刷新）。
- 新增过账配置窗（`MesPostConfigWindow`，L4）：开关/工位/单位/时机/中途取件/
  静态参数五件/三接口 URL 一窗全改；URL 格式校验（http/https）→ 写 ini →
  MesConfig 本进程立即生效；右列口径说明（现场人员看得懂）。
- 工单窗上传接线（API0032 物料上传，对照 HMI"批次物料上报"）：R/L 各一
  按钮，**先保存工单再后台提交**（把刚录条码落盘，过账现读盘上工单）；
  按钮可用性=过账服务已装配，使能校验 Poster 里再查一道（双保险防绕过）；
  **上传按按钮工位取槽**（不走全局配置 Side——自动进/出站才用配置工位）。
- `config.sample.ini` 加 `[CuringMes]` 节注释样例（铁律 8）。
- 测试基线 201 → 223（+22：`CuringMesPosterTests` 13 例——进站报文/Start
  时机/空途程跳过/出站链+累计/分钟换算/MES 拒绝不累计/WaitingGate 补出站/
  中途取件开关/Side=L/开关关/URL 空/物料上传+使能关/PostingChanged；
  `SlotCuringMachineTests` +3——三事件真实边沿发/对齐不发/门控放行触发/
  离开带相位与秒数；`CureStationViewModelTests` +6——工单上传先保存再触发/
  无服务按钮禁用/过账配置往返与 URL 校验/主窗过账配置门禁与热更新）。
- 验证：构建 0 警告 0 错误，测试 223/223；GUI 冒烟通过（Simulate=1 起
  CureStation.exe：三 json 自愈落盘 + Mock 流转触发状态机计时/取件日志 +
  出站链过账门槛日志"总开关未启用"——状态机→Poster→门槛全链路真进程打通）。

## V0.0.17（2026-10-28，P3.2 前半：四级权限 + 登录 + 工单信息页）

背景：P3.2 拆两半——工单页/权限页不依赖真机先做；单线过账（复用 MES 层
与 16 字段参数记忆）等 CIM W68 终版一起动。对照 `docs/参考界面/` 两张 HMI
（工单信息页 / 权限管理页）落地。

- Core 新增四级操作员账号体系（`Security/OperatorStore.cs`）：
  * `OperatorLevel.Level1~4`（操作员/班长/工程师/管理员，对照 HMI LEVEL.1~4，
    数字越大权限越高）；`operators` 表与 PLCtable 同库（备份拷一个 db3 全带走），
    密码哈希复用 PasswordHasher（PBKDF2 同款锁芯）。
  * **自建表不复用主程序 users 表**：两程序分机部署，账号是各程序私有资产
    （与 IDialogService"不下沉"同理）；主程序 admin/dev 零影响。
  * **防锁死红线**：最后一个 LEVEL.4 删除/降级一律拒绝——现场永远留一把
    最高权限钥匙；未知级别文本按 L1 兜底（安全侧：错降无害错升危险）。
  * 播种：空表播一个 L4 `admin`（初始密码 123456 只在 App 启动一处，上线必改）。
- Core 新增工单模型（`Curing/WorkOrderStore.cs`）：`config/workorder.json`
  ——R/L 双工位各 { 途程单号 RC_NO / 预设数量 / 已生产数量 } + 员工工号
  EMP_NO + 物料条码 BC_NO + 制程代码 PT_NO×2 + 物料上传使能。字段名与
  MesParamStore 16 字段刻意对齐（过账映射靠它）；运行时数据非配置，缺/坏
  回 null 自愈空工单，首次保存才落盘。
- CureStation 新增三窗（照点表/策略窗模式与 XAML 纪律）：
  * **登录窗**：四级操作员验证，级别带出做门禁依据（PasswordBox 事件转交
    同主程序 workaround）。
  * **工单信息窗**：R/L 双工位 + 基础信息 + 批次物料 + 制程代码，计数清零，
    保存落盘；**门禁在编辑区**——途程/数量/清零 L2+、制程代码 L3+、员工工号/
    条码 L1+，级别不够的编辑区禁用且 Flush 兜底回原值（防绕过界面直改）。
  * **权限管理窗**：账号列表 + 新增/改级/改密/删除，防锁死错误弹框展示。
- 主窗接线：顶栏加"登录/工单/权限"入口与操作员展示；**门禁统一出口**
  `Gate(级别, 功能)`——未登录先拦、级别不够提示所需级别；工艺策略 L3+、
  点表配置 L4+（登录语义变化：主窗功能入口现在必须先登录）。
- 顺手修 V0.0.15 遗留 bug：主窗顶栏"工艺策略"按钮 Grid.Column 与连接文本
  重叠（冒烟只看进程存活没看布局细节）；本轮顶栏重排一并修正。
- 测试基线 184 → 201（+17：OperatorStore 7（播种/验证/增删改/防锁死/级别
  工具）+ WorkOrderStore 3（往返/自愈）+ VM 7（门禁/登录/工单门禁与往返/
  权限管理往返））；既有用例两处适配：策略窗用例补登录（门禁生效）、
  MockBus 写点断言改"事件序列存在"（自动演示线程并发边沿的竞态安全）。
- 验证：构建 0 警告 0 错误，测试 201/201；GUI 冒烟通过（建库 + operators
  播种 admin/Level4 + 进程存活）。

## V0.0.16（2026-10-28，P3.1 补充：计时状态持久化做成可配置）

背景：V0.0.15 交付时"计时状态不持久化（断电重计）"登记为报价加价点；用户
拍板做成**可配置**——开关放工艺策略上（`CuringPolicy.PersistMinutes`，缺省
关=行为与 V0.0.15 完全一致），现场按工艺口径自行决定是否断电续算。

- `CuringPolicy` 新增 `PersistMinutes` 开关（跟激活策略走：热切换策略即切换
  持久化行为，无需重启；缺省 false=宁可重计也不放行没固化够的货）。
- Core 新增 `Curing/CuringStateStore`：`config/curingstate.json` 读写（运行时
  数据非配置——现场自动生成、不入库、删了即回退重计口径）；记录恢复所需
  最小集（仓位/相位/起点墙钟/锁定时长）。
- `SlotCuringMachine` 挂持久化：三个状态变化点（开计时/到点放行/取件清空）
  全量重写状态文件（文件几十行、边沿级频率，比定时落盘简单且不丢边沿）；
  `AlignFromSnapshot` 对齐时开关开则读记录续算——**断电期间流逝的时间自然
  计入固化时长**（Curing 按记录起点续算，Done 直接恢复绿灯）；没记录/开关
  关/文件坏 → 兜底重计，永不卡流程。WaitingGate 不落盘（没开计时，重启
  重走门控）。落盘失败只记日志下次再写（持久化是加分项不是命脉）。
- 工艺策略窗编辑区加"断电续算"勾选框（含说明文字，Sync/Flush 往返）。
- 测试基线 178 → 184（+6：持久化开计时/到点落盘、重启续算剩余分钟、
  Done 恢复绿灯、取件清记录、开关关零落盘且无视旧记录、坏文件兜底重计；
  VM 往返断言并入既有保存用例）。
- 验证：构建 0 警告 0 错误，测试 184/184；GUI 冒烟通过（旧策略文件无新
  字段反序列化兼容 → 开关关不生成状态文件，行为自证）。

## V0.0.15（2026-10-28，两程序拆分 P3.1：仓位状态机 + 工艺策略）

背景：P2.2（6 接口迁移与整线联调）仍等外部输入（D/R 终版地址 + CIM W68），
阻塞期间推进 P3 无阻塞项——固化程控核心：仓位状态机 + 工艺策略（参考
AgingTestSystem 配方管理模式）。用户拍板三项：①流程"到位即计时"（托盘到位→
黄灯计时→到点绿灯放行→托盘离开灭灯回空闲，红灯留异常语义）；②固化时长
三种粒度全兼容；③光栅角色可配 + 做工艺策略界面。

- Core 新增 Curing 家族（`src/PlcMesBridge.Core/Curing/`）：
  * `CuringPolicy`：策略模型——时长**三级覆盖**（全局打底 → 每层覆盖 →
    每仓位覆盖，`ResolveMinutes` 一处收敛优先级，三种粒度可混用；不用
    "三选一模式"枚举，避免"选错模式大面积回退"的坑）+ 光栅门控开关
    （true=左右光栅都灭且托盘在架才开计时，防人手在架内就计时；没绑光栅的
    仓位门控自动放行）+ `CuringPolicyValidator`（时长 ≥1 分钟/层号 1~4/
    仓位键"架-层"格式/库内策略名唯一）。
  * `CuringPolicyStore`：`config/curingpolicy.json` 读写（策略库 + 激活名），
    缺/坏回 null 自愈缺省库（"标准固化" 120 分钟，现场工艺时长确认后改），
    照点表 Store 自愈语义。
  * `SlotCuringMachine`：仓位状态机——边沿驱动（托盘启停流程/光栅放行门控）
    + 1s 心跳 `Pump`（到期放行 + 灯写补写自愈 + 门控兜底推进）。线程模型
    （铁律 2）：灯写全在后台（宿主转发边沿 + 内部 Timer，绝不进 UI 线程），
    测试注入虚拟时钟手动 Pump。自愈三件：灯写"期望 vs 已写"去重比对
    （稳态零网络写，失败下轮自动补写）；断连恢复 `AlignFromSnapshot`
    （托盘在架重新计时——计时状态不持久化属报价加价点登记项，宁可重计也
    不放行没固化够的货）；策略热切换（进行中仓位按开计时那一刻锁定的时长
    走完，改参数不影响在制品）。日志只记边沿（铁律 4）。
- CureStation：新增**工艺策略窗**（策略库增删选 + 编辑面板 + 每仓位覆盖
  行表 + 激活并生效/仅保存，照点表窗"编辑面板+Flush"模式与 XAML 纪律）；
  主窗顶栏加"工艺策略"入口与"策略：XX"展示；仓位卡状态文本升级状态机
  口径（空闲/待门控/计时中 剩X分/放行），灯色反推口径保留给空闲仓位。
- `MainViewModel` 接线：总线边沿后台直转状态机（不碰 UI）；断连恢复对齐；
  重连改"先建/换妥新通道再停旧总线"（状态机不摸已释放对象，`Attach` 换
  通道流程状态不丢）；策略自愈静默落盘（与点表弹框自愈不同——有缺省值
  可直接跑，不扰操作员）。
- 测试基线 148 → 178（+30：策略解析/校验/存储 9 + 状态机 13 + VM 6 +
  主窗集成 2）；新增 `ScriptedPointBus` 桩（手动发边沿 + 录灯写 + 可控快照，
  与 MockPointTableBus 自动演示互补）。
- 验证：构建 0 警告 0 错误，测试 178/178 全绿；GUI 冒烟通过（CureStation.exe
  存活 + pointtable.json/curingpolicy.json 双自愈落盘）。

## V0.0.14（2026-10-28，两程序拆分 P2.1：删三菱 + Modbus 统一底座）

背景：P2 第一步——程序②（现 PlcMesBridge，Phase 2 演化 CimGateway）删三菱
链路，真机底座统一 Modbus TCP。用户拍板"物理搬运即可"= 彻底删，git 历史
可找回。本轮为纯结构改造（6 接口业务逻辑零改动），真机联调待 D/R 终版
地址与 CIM W68。

- 新增 `src/PlcMesBridge.Core/Comms/ModbusStationClient.cs`：`IPlcClient`
  的 Modbus TCP 实现，包装子模块 `ModbusTcpMasterClient`（手动超时建连/
  锁串行化/断连标记/5s 节流重连全套现成，零自造轮子）。三个关键语义
  集中在此一处（`ModbusChannelTests` 24 例锁定）：
  ① 地址="剥 R/D 前缀取数字"=Modbus 协议地址（0x0000 起保持寄存器），
     残缺/超 65535 拒收——配错地址当场走失败链，绝不误写寄存器 0；
  ② 字符串打包 2 字节/寄存器 big-endian（首字节装高位，"NG"→0x4E47），
     `PackAscii/UnpackAscii` 纯函数，现场校准只改这一处；
  ③ 分帧：FC03 单帧 ≤125 / FC10 ≤123 寄存器（网关 DataLength=799 字节
     ≈400 寄存器必须分块），区间越界整次失败。
- 删除清单（单机链整链）：`MelsecPlcClient`、`SingleMachineCoordinator`、
  `ProductRowViewModel`、`ModeSwitchViewModel`、`ModeSwitchWindow`(+.cs)、
  `SingleMachineTests`/`SingleMachineEdgeTests`/`CoordinatorCustomAddrTests`；
  配置模型删 `RunMode` 枚举/`[setting] Mode`/`[SinglePLC]` 段/`PLCAscii`
  协议键（残留旧 ini 键无害，程序不再读取）；主窗删单机视图与"运行模式"
  菜单（多机网关单视图）；PLC 配置窗三页→两页（多机 PLC/接口地址）。
- 保留与沿用：`IPlcClient` 抽象与 `SimulatedPlcClient`（测试地基）不动；
  `RunModeSettings` 类名不改（只剩登录记忆+门禁，注释说明历史）；多机
  网关 6 接口 JSON 段与触发/回写地址全部原样（MES 侧约定零改动）。
- 测试基线 156 → 148：删单机链连带用例 32 例（工程必然，随业务删除），
  新增 `ModbusChannelTests` 24 例；`ConnectionTesterTests` 真机不可达
  用例改走 `ModbusStationClient`（顺带覆盖新通道连接失败链）。
- 顺手清 CureStation 两个既有警告：CS0105 重复 using；CS0649
  `MainViewModel._config` 未赋值——实为真 bug（重连按钮永远传 null
  点表→显示"未配置设备"），启动/点表保存两处补记当前点表。
- `config.sample.ini` 同步：删 Mode/单机注释/PLCAscii/MESPLCALarm/
  [SinglePLC] 段；PLCPorts 缺省改 502（Modbus TCP 标准端口）；头注释
  说明真机统一 Modbus TCP 与地址语义。
- 验证：构建 0 警告 0 错误，测试 148/148 全绿；双程序 GUI 冒烟通过
  （PlcMesBridge.exe 存活 + config/DataBase/log 自愈落盘；CureStation.exe
  存活 + pointtable.json 自愈落盘）。

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
