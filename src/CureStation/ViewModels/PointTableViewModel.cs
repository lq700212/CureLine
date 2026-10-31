// =========================================================================
// PointTableViewModel：点表配置窗编排（"点点鼠标完成 IO 端点配置"的核心）
//
// 干什么：
//   ① 加载 config/pointtable.json → 设备列表 + 选中设备的绑定网格
//      （V0.0.45 修：切换设备设备名/IP/高级参数随选中回显 SyncDeviceEditor）；
//   ② 一键"生成缺省模板"/"批量生成机台耦合器"（IoPointTableStore 两生成器）；
//   ③ **绑定表直编**（V0.0.45 起，绑定编辑面板已删）：行内直接改 方向/块/
//      角色/架/层/功能名（PointBindingRow 写穿 Raw 所见即所得），加删行走
//      AddBindingCommand/DeleteBindingCommand（双边同步 Raw 集合与行集合）；
//   ④ 保存前 PointTableValidator 全量校验（有误绝不落盘，弹错当场拦）；
//   ⑤ "测试连接"后台 Task 直测（IP/端口/寄存器读回）不冻界面。
// 事件流向：保存成功 → CloseDialog（VM 不 new Window，关窗经 DialogResult；
//   测试用 FakeDialogService 断言调用，见 tests 工程用例）。
// =========================================================================

using System.Collections.ObjectModel;
using CureLine.Core.Comms;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>绑定表行：网格直编列（写穿 Raw）+ 只读派生文本（点号/仓位）。</summary>
/// <summary>
/// 绑定表行（V0.0.45 起表格直编）：可编辑属性**写穿 Raw**（所见即所得，无独立
/// 编辑面板草稿——物理点唯一/语义键唯一冲突由保存前校验统一拦，行内不重复拦）；
/// 派生文本（点号/仓位）随编辑即时刷新。Bit/点号只读（点号是块内物理位置，改它
/// 等于换点——加删行才是正路）。
/// </summary>
public sealed class PointBindingRow : ViewModelBase
{
    public IoPointBinding Raw { get; }
    private readonly PointTableViewModel _owner;

    public PointBindingRow(IoPointBinding raw, PointTableViewModel owner)
    {
        Raw = raw;
        _owner = owner;
    }

    // ---- 行内可编辑列（写穿 Raw） ----
    public int Block { get => Raw.Block; set => Raw.Block = value; }
    public bool IsInput { get => Raw.IsInput; set { Raw.IsInput = value; RefreshDisplay(); } }
    public IoPointRole Role { get => Raw.Role; set { Raw.Role = value; RefreshDisplay(); } }
    public int Rack { get => Raw.Rack; set { Raw.Rack = value; Raise(nameof(SlotText)); } }
    public int Level { get => Raw.Level; set { Raw.Level = value; Raise(nameof(SlotText)); } }
    public string FunctionName { get => Raw.FunctionName; set => Raw.FunctionName = value ?? ""; }

    // ---- 只读派生列 ----
    public string BitText => $"{Raw.Bit} ({(Raw.IsInput ? "X" : "Y")}{Raw.Bit:X1})";
    public string SlotText => Raw.Role == IoPointRole.Reserved
        ? "—" : $"架{Raw.Rack}层{Raw.Level}";

    /// <summary>方向/角色变化 → 派生文本刷新（点号 X/Y 前缀与仓位显隐随之）。</summary>
    public void RefreshDisplay()
    {
        Raise(nameof(BitText));
        Raise(nameof(SlotText));
    }
}

public sealed class PointTableViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private bool _closed;

    /// <summary>
    /// 角色下拉数据源：Text=中文文案（与左侧表格"角色"列同一来源
    /// IoPointRoleInfo.ToText，两处永远同口径），Value=英文枚举——下拉只改
    /// 显示改存储，EditRole/ApplyBinding/落盘 JSON 全链仍是英文枚举名。
    /// </summary>
    public sealed record RoleOption(IoPointRole Value, string Text);

    public static IReadOnlyList<RoleOption> RoleOptions { get; } =
        Enum.GetValues<IoPointRole>()
            .Select(r => new RoleOption(r, IoPointRoleInfo.ToText(r)))
            .ToArray();

    /// <summary>方向下拉数据源（表格直编列；SelectedValue=bool 走 IsInput 写穿）。</summary>
    public sealed record DirectionOption(bool Value, string Text);

    public static IReadOnlyList<DirectionOption> DirectionOptions { get; } =
        new DirectionOption[] { new(true, "输入(DI)"), new(false, "输出(DO)") };

    public ObservableCollection<IoCouplerDevice> Devices { get; } = new();

    private IoCouplerDevice? _selectedDevice;
    public IoCouplerDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
            {
                SyncDeviceEditor();   // V0.0.45 修：设备名/IP/高级参数随选中设备回显（此前漏调，切换后编辑区停在上台设备的值）
                ReloadBindings();
                Raise(nameof(CurrentDeviceText));
                AddBindingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<PointBindingRow> Bindings { get; } = new();

    private PointBindingRow? _selectedBinding;
    public PointBindingRow? SelectedBinding
    {
        get => _selectedBinding;
        set
        {
            // 表格选中行变化（DataGrid SelectedItem 双向绑定驱动；换设备/删行也走这）
            // —— V0.0.45 表格直编后选中行只服务"删除选中行"按钮的可用性。
            if (Set(ref _selectedBinding, value))
                DeleteBindingCommand.RaiseCanExecuteChanged();
        }
    }

    // ---- 缺省模板生成参数 ----
    private string _templateIp = "192.168.1.20";
    public string TemplateIp
    {
        get => _templateIp;
        set => Set(ref _templateIp, value);
    }
    private string _templateRacks = "10";
    public string TemplateRacks
    {
        get => _templateRacks;
        set => Set(ref _templateRacks, value);
    }
    private string _templateLevels = "3";
    public string TemplateLevels
    {
        get => _templateLevels;
        set => Set(ref _templateLevels, value);
    }
    // 双架部署（V0.0.20）：第二台耦合器起始架号填 11，模板生成架 11~20——
    // 全局架号必须唯一（跨设备语义查重会拦撞号），单架保持 1 不用管。
    private string _templateRackStart = "1";
    public string TemplateRackStart
    {
        get => _templateRackStart;
        set => Set(ref _templateRackStart, value);
    }

    // ---- 批量生成机台耦合器（V0.0.41 现场口径：一台机台 10 列 × 3 层，
    //      每列 1 台町洋 GFNB-1A3A——缺省参数即现场现值，点一下就配完 10 台）----
    private string _batchCount = "10";
    public string BatchCountText { get => _batchCount; set => Set(ref _batchCount, value); }
    private string _batchIpFirst = "192.168.1.20";
    public string BatchIpFirstText { get => _batchIpFirst; set => Set(ref _batchIpFirst, value); }
    private string _batchIpStep = "1";
    public string BatchIpStepText { get => _batchIpStep; set => Set(ref _batchIpStep, value); }

    // 设备级编辑
    private string _deviceName = "IO耦合器1";
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }
    private string _deviceIp = "192.168.1.20";
    public string DeviceIp { get => _deviceIp; set => Set(ref _deviceIp, value); }

    // ---- 设备级高级参数（V0.0.20 全 UI 化——此前只能手编 pointtable.json）----
    // 地址类文本支持 "0x" 前缀十六进制与十进制两种写法（现场对照说明书抄哪种都行）；
    // 非法输入保持原值不崩（保存前校验还会兜底），对齐绑定编辑面板同款口径。
    private string _editDevicePort = "502";
    public string EditDevicePort { get => _editDevicePort; set => Set(ref _editDevicePort, value ?? ""); }
    private string _editDeviceUnitId = "1";
    public string EditDeviceUnitId { get => _editDeviceUnitId; set => Set(ref _editDeviceUnitId, value ?? ""); }
    private string _editDeviceDiStart = "0x1000";
    public string EditDeviceDiStart { get => _editDeviceDiStart; set => Set(ref _editDeviceDiStart, value ?? ""); }
    private string _editDeviceDoStart = "0x1010";
    public string EditDeviceDoStart { get => _editDeviceDoStart; set => Set(ref _editDeviceDoStart, value ?? ""); }
    private string _editDeviceStride = "0x20";
    public string EditDeviceStride { get => _editDeviceStride; set => Set(ref _editDeviceStride, value ?? ""); }
    private string _editDevicePointsPerBlock = "16";
    public string EditDevicePointsPerBlock { get => _editDevicePointsPerBlock; set => Set(ref _editDevicePointsPerBlock, value ?? ""); }
    private string _editDeviceTimeoutMs = "3000";
    public string EditDeviceTimeoutMs { get => _editDeviceTimeoutMs; set => Set(ref _editDeviceTimeoutMs, value ?? ""); }
    private string _editDevicePollIntervalMs = "250";
    public string EditDevicePollIntervalMs { get => _editDevicePollIntervalMs; set => Set(ref _editDevicePollIntervalMs, value ?? ""); }
    private bool _editDeviceInvertInputs;
    public bool EditDeviceInvertInputs { get => _editDeviceInvertInputs; set => Set(ref _editDeviceInvertInputs, value); }
    private bool _editDeviceInvertOutputs;
    public bool EditDeviceInvertOutputs { get => _editDeviceInvertOutputs; set => Set(ref _editDeviceInvertOutputs, value); }

    public bool CloseDialog
    {
        get => _closed;
        private set => Set(ref _closed, value);
    }

    public RelayCommand AddDeviceCommand { get; }
    public RelayCommand RemoveDeviceCommand { get; }
    public RelayCommand GenerateTemplateCommand { get; }
    public RelayCommand GenerateMachineCommand { get; }
    public RelayCommand AddBindingCommand { get; }
    public RelayCommand DeleteBindingCommand { get; }
    public RelayCommand TestConnectionCommand { get; }
    public RelayCommand TestAllCommand { get; }
    public RelayCommand SaveCommand { get; }

    public PointTableViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        AddDeviceCommand = new RelayCommand(AddDevice);
        RemoveDeviceCommand = new RelayCommand(RemoveDevice, () => SelectedDevice != null);
        GenerateTemplateCommand = new RelayCommand(GenerateTemplate);
        GenerateMachineCommand = new RelayCommand(GenerateMachine);
        AddBindingCommand = new RelayCommand(AddBinding, () => SelectedDevice != null);
        DeleteBindingCommand = new RelayCommand(DeleteBinding, () => SelectedBinding != null);
        TestConnectionCommand = new RelayCommand(TestConnection, () => !IsTesting);
        TestAllCommand = new RelayCommand(TestAll, () => !IsTesting);
        SaveCommand = new RelayCommand(Save);

        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        Devices.Clear();
        foreach (var d in IoPointTableStore.Load()?.Devices ?? new())
            Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();   // setter 联动 SyncDeviceEditor+ReloadBindings
    }

    /// <summary>把选中设备回显到设备编辑区（选中变化/Save 均调）。</summary>
    private void SyncDeviceEditor()
    {
        if (SelectedDevice == null)
            return;
        DeviceName = SelectedDevice.Name;
        DeviceIp = SelectedDevice.Ip;
        // 高级参数回显：地址类按 Modbus 习惯格式化 0xXXXX，其余十进制
        EditDevicePort = SelectedDevice.Port.ToString();
        EditDeviceUnitId = SelectedDevice.UnitId.ToString();
        EditDeviceDiStart = FmtAddr(SelectedDevice.DiStartAddress);
        EditDeviceDoStart = FmtAddr(SelectedDevice.DoStartAddress);
        EditDeviceStride = FmtAddr(SelectedDevice.BlockStrideRegisters);
        EditDevicePointsPerBlock = SelectedDevice.PointsPerBlock.ToString();
        EditDeviceTimeoutMs = SelectedDevice.TimeoutMs.ToString();
        EditDevicePollIntervalMs = SelectedDevice.PollIntervalMs.ToString();
        EditDeviceInvertInputs = SelectedDevice.InvertInputs;
        EditDeviceInvertOutputs = SelectedDevice.InvertOutputs;
    }

    private static string FmtAddr(int value) => $"0x{value:X4}";

    /// <summary>
    /// 地址/数字文本解析：带 "0x" 前缀按十六进制，否则十进制——现场对照
    /// IO 表说明书两种抄法都支持；解析失败返回 false（调用方保持原值）。
    /// </summary>
    private static bool TryParseNum(string? text, out int value)
    {
        value = 0;
        string s = (text ?? "").Trim();
        if (s.Length == 0)
            return false;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value)
            : int.TryParse(s, out value);
    }

    private void ReloadBindings()
    {
        Bindings.Clear();
        foreach (var b in SelectedDevice?.Bindings ?? new())
            Bindings.Add(new PointBindingRow(b, this));
        // 缺省选中第一行（"删除选中行"按钮的定位起点）
        SelectedBinding = Bindings.FirstOrDefault();
    }

    private void AddDevice()
    {
        var device = new IoCouplerDevice
        {
            Name = $"IO耦合器{Devices.Count + 1}",
            Ip = DeviceIp,
        };
        Devices.Add(device);
        SelectedDevice = device;   // setter 联动 SyncDeviceEditor+ReloadBindings
    }

    private void RemoveDevice()
    {
        if (SelectedDevice == null)
            return;
        var d = SelectedDevice;
        SelectedDevice = null;
        Devices.Remove(d);
        SelectedDevice = Devices.FirstOrDefault();
        if (SelectedDevice == null)
        {
            Bindings.Clear();
            // 全删后 setter 没触发（null→null），编辑区/提示手动清一次
            DeviceName = "";
            DeviceIp = "";
            Raise(nameof(CurrentDeviceText));
        }
    }

    /// <summary>一键缺省模板：按 IP/起始架号/架数/层数生成本选中设备的全套绑定（覆盖原绑定）。</summary>
    private void GenerateTemplate()
    {
        if (!int.TryParse(TemplateRackStart, out var rackStart) || rackStart < 1)
        {
            _dialogs.ShowMessage("起始架号需要 ≥1 的数字（第二台固化架填 11）。", "缺省模板");
            return;
        }
        if (!int.TryParse(TemplateRacks, out var racks) || racks < 1)
        {
            _dialogs.ShowMessage("架数需要 ≥1 的数字。", "缺省模板");
            return;
        }
        if (!int.TryParse(TemplateLevels, out var levels) || levels is < 1 or > 4)
        {
            _dialogs.ShowMessage("每架层数需要 1~4（每层占 4 位）。", "缺省模板");
            return;
        }

        var template = IoPointTableStore.BuildDefaultTemplate(
            DeviceName, TemplateIp, racks, levels, rackStart);
        // 保留当前设备在列表里的名字（模板用编辑区名字生成，回填即可）
        template.Devices[0].Ip = TemplateIp;
        int index = SelectedDevice == null ? -1 : Devices.IndexOf(SelectedDevice);
        if (index >= 0)
        {
            Devices[index] = template.Devices[0];
            SelectedDevice = template.Devices[0];
        }
        else
        {
            Devices.Add(template.Devices[0]);
            SelectedDevice = template.Devices[0];
        }
        // SelectedDevice setter 已联动 ReloadBindings 重建绑定网格并回显首行，
        // 此处不再重复调（改干净改彻底：双份口径容易日后改一半漏一半）。
        _dialogs.ShowMessage(
            $"已按 IO 表布局生成缺省模板：{racks} 架 × {levels} 层，" +
            $"共 {template.Devices[0].Bindings.Count} 点。" +
            "\n建议保存后到现场核对 IP 与首辅点位。", "缺省模板已生成");
    }

    /// <summary>
    /// 表格【＋添加行】：往选中设备加一行预留位（块1·位3·输入·预留——缺省值
    /// 最接近"生成器没铺到的预留位"场景），Raw 集合与行集合双边同步加，选中
    /// 新行（用户接着在表格里直改角色/架/层）。
    /// </summary>
    private void AddBinding()
    {
        if (SelectedDevice == null)
            return;
        var binding = new IoPointBinding
        {
            Block = 1,
            Bit = 3,
            IsInput = true,
            Role = IoPointRole.Reserved,
            Rack = 1,
            Level = 1,
            FunctionName = "",
        };
        SelectedDevice.Bindings.Add(binding);
        var row = new PointBindingRow(binding, this);
        Bindings.Add(row);
        SelectedBinding = row;
    }

    /// <summary>表格【－删除选中行】：Raw 集合与行集合双边同步删（保存前不落盘，
    /// 误删可关窗不保存找回）。</summary>
    private void DeleteBinding()
    {
        if (SelectedBinding == null || SelectedDevice == null)
            return;
        SelectedDevice.Bindings.Remove(SelectedBinding.Raw);
        int index = Bindings.IndexOf(SelectedBinding);
        Bindings.Remove(SelectedBinding);
        // 选中替代行（或清空）：保持"删除选中行"按钮状态联动
        SelectedBinding = Bindings.Count > 0
            ? Bindings[Math.Clamp(index, 0, Bindings.Count - 1)]
            : null;
    }

    /// <summary>"测试连接"：后台 Task 直测当前选中设备（编辑区草稿值先刷进设备——
    /// 先测再存的体验：IP 改了没保存也能测到新值），结果进列表 + 弹框。</summary>
    private async void TestConnection()
    {
        SyncDeviceFromEditor();
        if (SelectedDevice == null)
        {
            _dialogs.ShowMessage("先添加设备再测试。", "测试连接");
            return;
        }
        var device = SelectedDevice;
        SetTesting(true);
        try
        {
            var (ok, msg) = await Task.Run(() => IoCouplerConnectionTester.Test(device));
            AddTestResult(device.Name, device.Ip, ok, msg);
            _dialogs.ShowMessage(msg, "测试连接");
        }
        finally
        {
            SetTesting(false);
        }
    }

    /// <summary>"测试全部"：逐台后台跑连通测试（一台一报，结果行实时进列表）。
    /// 现场配完 10 台点一下，哪台没连上一眼定位（结果含设备名/IP/NG 原因）。</summary>
    private async void TestAll()
    {
        if (Devices.Count == 0)
        {
            _dialogs.ShowMessage("先添加设备再测试。", "测试全部");
            return;
        }
        // 编辑区草稿值先刷进选中设备（对齐单台测试口径，防测到旧值）
        SyncDeviceFromEditor();
        var devices = Devices.ToList();
        SetTesting(true);
        try
        {
            foreach (var device in devices)
            {
                var (ok, msg) = await Task.Run(() => IoCouplerConnectionTester.Test(device));
                AddTestResult(device.Name, device.Ip, ok, msg);
            }
        }
        finally
        {
            SetTesting(false);
        }
    }

    // ---- 连通测试状态与结果列表（测试期间按钮灰，逐台出结果）----
    private bool _testing;
    public bool IsTesting
    {
        get => _testing;
        private set
        {
            if (Set(ref _testing, value))
            {
                TestConnectionCommand.RaiseCanExecuteChanged();
                TestAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void SetTesting(bool value) => UiInvoke(() => IsTesting = value);

    /// <summary>结果行进列表（UiInvoke 封送：后台 Task 回调直接调也安全）。</summary>
    private void AddTestResult(string name, string ip, bool ok, string message) =>
        UiInvoke(() =>
        {
            TestResults.Insert(0, new TestResultRow(name, ip, ok, message));
            // 只留最近 50 条防刷爆（现场反复点测试的场景）
            while (TestResults.Count > 50)
                TestResults.RemoveAt(TestResults.Count - 1);
        });

    /// <summary>连通测试结果行（测试全部列表的列：设备名/IP/OK·NG/原因）。</summary>
    public sealed record TestResultRow(string DeviceName, string Ip, bool Ok, string Message)
    {
        public string ResultText => Ok ? "OK" : "NG";
        public string TimeText => DateTime.Now.ToString("HH:mm:ss");
    }

    public ObservableCollection<TestResultRow> TestResults { get; } = new();

    /// <summary>中栏顶部"当前设备"提示（多台部署排障：这屏绑定属于哪台耦合器）。</summary>
    public string CurrentDeviceText => SelectedDevice == null
        ? "当前设备：（未选中）"
        : $"当前设备：{SelectedDevice.Name}（{SelectedDevice.Ip}:{SelectedDevice.Port}）";

    /// <summary>保存：先全量校验（错弹框不落盘），再写 pointtable.json。
    /// V0.0.45 表格直编后绑定行已实时写穿 Raw，无"编辑面板未应用"丢值问题。</summary>
    private void Save()
    {
        FlushDeviceEditor();
        var validation = PointTableValidator.Validate(BuildConfig());
        if (!validation.Ok)
        {
            _dialogs.ShowMessage(string.Join("\r\n", validation.Errors), "保存失败（校验不过）");
            return;
        }
        IoPointTableStore.Save(BuildConfig());
        CloseDialog = true;
    }

    private void FlushDeviceEditor() => SyncDeviceFromEditor();

    private void SyncDeviceFromEditor()
    {
        if (SelectedDevice == null)
            return;
        var d = SelectedDevice;
        d.Name = DeviceName;
        d.Ip = DeviceIp;
        // 高级参数：解析失败保持原值不崩（保存前校验兜底），对齐绑定编辑面板口径
        if (TryParseNum(EditDevicePort, out var port) && port is >= 1 and <= 65535)
            d.Port = port;
        if (TryParseNum(EditDeviceUnitId, out var unit) && unit is >= 1 and <= 255)
            d.UnitId = (byte)unit;
        if (TryParseNum(EditDeviceDiStart, out var di) && di is >= 0 and <= 65535)
            d.DiStartAddress = (ushort)di;
        if (TryParseNum(EditDeviceDoStart, out var do_) && do_ is >= 0 and <= 65535)
            d.DoStartAddress = (ushort)do_;
        if (TryParseNum(EditDeviceStride, out var stride) && stride >= 1)
            d.BlockStrideRegisters = (ushort)stride;
        if (TryParseNum(EditDevicePointsPerBlock, out var ppb) && ppb is >= 1 and <= 16)
            d.PointsPerBlock = ppb;
        if (TryParseNum(EditDeviceTimeoutMs, out var timeout) && timeout >= 100)
            d.TimeoutMs = timeout;
        if (TryParseNum(EditDevicePollIntervalMs, out var poll) && poll >= 50)
            d.PollIntervalMs = poll;
        d.InvertInputs = EditDeviceInvertInputs;
        d.InvertOutputs = EditDeviceInvertOutputs;
        Raise(nameof(CurrentDeviceText)); // 名称/IP 编辑后中栏提示同步
    }

    /// <summary>
    /// 批量生成机台耦合器（V0.0.41）：台数/IP 首址/IP 步长 → BuildMachineTemplate
    /// 一次生成 N 台（每台 1 架 3 层、GFNB 占位地址），覆盖重建设备列表。
    /// 确认弹框在前——覆盖动作不可撤销，防误点冲掉已配好的表。
    /// </summary>
    private void GenerateMachine()
    {
        if (!int.TryParse(BatchCountText, out var count) || count is < 1 or > 99)
        {
            _dialogs.ShowMessage("台数需要 1~99 的数字。", "批量生成");
            return;
        }
        if (!int.TryParse(BatchIpStepText, out var step) || step is < 0 or > 255)
        {
            _dialogs.ShowMessage("IP 步长需要 0~255 的数字（多台不能为 0）。", "批量生成");
            return;
        }
        string ipFirst = BatchIpFirstText.Trim();
        if (!System.Net.IPAddress.TryParse(ipFirst, out _))
        {
            _dialogs.ShowMessage($"IP 首址非法：'{ipFirst}'。", "批量生成");
            return;
        }
        if (!_dialogs.Confirm(
                $"将按现值覆盖重建全部设备：{count} 台耦合器（IP {ipFirst} 起每台 +{step}，" +
                $"每台 1 架 3 层）。\r\n现有设备与绑定会被清掉，确定继续？", "批量生成"))
            return;

        try
        {
            var template = IoPointTableStore.BuildMachineTemplate(
                count, rackStart: 1, levelsPerRack: 3, ipFirst, step);
            Devices.Clear();
            foreach (var d in template.Devices)
                Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault();
            SyncDeviceEditor();
            _dialogs.ShowMessage(
                $"已生成 {count} 台耦合器模板：IP {template.Devices[0].Ip} ~ " +
                $"{template.Devices[^1].Ip}，每台 1 架 3 层共 {template.Devices[0].Bindings.Count} 点。" +
                "\nIP 已按现场现值预填，如需调整逐台改名/改 IP 后保存即可。", "批量生成完成");
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage($"批量生成失败：{ex.Message}", "批量生成");
        }
    }

    /// <summary>合成当前 VM 状态到配置对象（保存/校验共用）。</summary>
    private PointTableConfig BuildConfig() => new() { Devices = [.. Devices] };
}
