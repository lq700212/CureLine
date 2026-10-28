// =========================================================================
// PointTableViewModel：点表配置窗编排（"点点鼠标完成 IO 端点配置"的核心）
//
// 干什么：
//   ① 加载 config/pointtable.json → 设备列表 + 选中设备的绑定网格；
//   ② 一键"生成缺省模板"（IP+架数+层数 → IoPointTableStore.BuildDefaultTemplate）；
//   ③ 绑定行选中 → 编辑面板（块/位/方向/角色/架/层/功能名）点"应用修改"落行；
//   ④ 保存前 PointTableValidator 全量校验（有误绝不落盘，弹错当场拦）；
//   ⑤ "测试连接"后台 Task 直测（IP/端口/寄存器读回）不冻界面。
// 事件流向：保存成功 → CloseDialog（VM 不 new Window，关窗经 DialogResult；
//   测试用 FakeDialogService 断言调用，见 tests 工程用例）。
// =========================================================================

using System.Collections.ObjectModel;
using PlcMesBridge.Core.Comms;
using PlcMesBridge.ViewModels;

namespace CureStation.ViewModels;

/// <summary>绑定行包装：网格列显示（角色中文/方向中文）+ 编辑面板互写出入口。</summary>
public sealed class PointBindingRow : ViewModelBase
{
    public IoPointBinding Raw { get; }
    private readonly PointTableViewModel _owner;

    public PointBindingRow(IoPointBinding raw, PointTableViewModel owner)
    {
        Raw = raw;
        _owner = owner;
    }

    public string BlockText => Raw.Block.ToString();
    public string BitText => $"{Raw.Bit} ({(Raw.IsInput ? "X" : "Y")}{Raw.Bit:X1})";
    public string DirectionText => Raw.IsInput ? "输入" : "输出";
    public string RoleText => IoPointRoleInfo.ToText(Raw.Role);
    public string SlotText => Raw.Role == IoPointRole.Reserved
        ? "—" : $"架{Raw.Rack}层{Raw.Level}";

    /// <summary>选中行变更 → 编辑面板回显（窗侧 SelectedIndex 变化调）。</summary>
    public void RefreshDisplay()
    {
        Raise(nameof(BitText));
        Raise(nameof(RoleText));
        Raise(nameof(SlotText));
        Raise(nameof(DirectionText));
    }

    public void NotifyChangedFromEditor() => RefreshDisplay();
}

public sealed class PointTableViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private bool _closed;

    /// <summary>角色下拉的数据源（控件下拉框绑定；工厂方法算不能在 XAML 里 new）。</summary>
    public static IoPointRole[] RoleOptions { get; } =
        Enum.GetValues<IoPointRole>();

    public ObservableCollection<IoCouplerDevice> Devices { get; } = new();

    private IoCouplerDevice? _selectedDevice;
    public IoCouplerDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
                ReloadBindings();
        }
    }

    public ObservableCollection<PointBindingRow> Bindings { get; } = new();

    private PointBindingRow? _selectedBinding;
    public PointBindingRow? SelectedBinding
    {
        get => _selectedBinding;
        set => Set(ref _selectedBinding, value);
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

    // ---- 编辑面板（选中绑定行回显可改）----
    private string _editBlock = "1";
    public string EditBlock { get => _editBlock; set => Set(ref _editBlock, value); }
    private string _editBit = "0";
    public string EditBit { get => _editBit; set => Set(ref _editBit, value); }
    private bool _editIsInput = true;
    public bool EditIsInput { get => _editIsInput; set => Set(ref _editIsInput, value); }
    private IoPointRole _editRole = IoPointRole.Reserved;
    public IoPointRole EditRole { get => _editRole; set => Set(ref _editRole, value); }
    private string _editRack = "0";
    public string EditRack { get => _editRack; set => Set(ref _editRack, value); }
    private string _editLevel = "0";
    public string EditLevel { get => _editLevel; set => Set(ref _editLevel, value); }
    private string _editFunction = "";
    public string EditFunction { get => _editFunction; set => Set(ref _editFunction, value); }

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
    public RelayCommand ApplyBindingCommand { get; }
    public RelayCommand DeleteBindingCommand { get; }
    public RelayCommand TestConnectionCommand { get; }
    public RelayCommand SaveCommand { get; }

    public PointTableViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        AddDeviceCommand = new RelayCommand(AddDevice);
        RemoveDeviceCommand = new RelayCommand(RemoveDevice, () => SelectedDevice != null);
        GenerateTemplateCommand = new RelayCommand(GenerateTemplate);
        ApplyBindingCommand = new RelayCommand(ApplyBinding, () => SelectedBinding != null);
        DeleteBindingCommand = new RelayCommand(DeleteBinding, () => SelectedBinding != null);
        TestConnectionCommand = new RelayCommand(TestConnection);
        SaveCommand = new RelayCommand(Save);

        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        Devices.Clear();
        foreach (var d in IoPointTableStore.Load()?.Devices ?? new())
            Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();
        SyncDeviceEditor();
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
        // 缺省选中第一行给编辑面板一个起点
        SelectedBinding = Bindings.FirstOrDefault();
        SyncEditorFromSelected();
    }

    private void SyncEditorFromSelected()
    {
        if (SelectedBinding == null)
            return;
        var r = SelectedBinding.Raw;
        EditBlock = r.Block.ToString();
        EditBit = r.Bit.ToString();
        EditIsInput = r.IsInput;
        EditRole = r.Role;
        EditRack = r.Rack.ToString();
        EditLevel = r.Level.ToString();
        EditFunction = r.FunctionName;
    }

    private void AddDevice()
    {
        var device = new IoCouplerDevice
        {
            Name = $"IO耦合器{Devices.Count + 1}",
            Ip = DeviceIp,
        };
        Devices.Add(device);
        SelectedDevice = device;
        SyncDeviceEditor();
    }

    private void RemoveDevice()
    {
        if (SelectedDevice == null)
            return;
        var d = SelectedDevice;
        SelectedDevice = null;
        Devices.Remove(d);
        SelectedDevice = Devices.FirstOrDefault();
        SyncDeviceEditor();
        if (SelectedDevice == null)
            Bindings.Clear();
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
        ReloadBindings();
        _dialogs.ShowMessage(
            $"已按 IO 表布局生成缺省模板：{racks} 架 × {levels} 层，" +
            $"共 {template.Devices[0].Bindings.Count} 点。" +
            "\n建议保存后到现场核对 IP 与首辅点位。", "缺省模板已生成");
    }

    /// <summary>编辑面板 → 选中行（回写 Raw + 行显示刷新）。数字非法保持原值并提示。</summary>
    private void ApplyBinding()
    {
        if (SelectedBinding?.Raw == null)
            return;
        var r = SelectedBinding.Raw;

        r.Block = ParseInt(EditBlock, r.Block, "块号");
        r.Bit = ParseInt(EditBit, r.Bit, "位号");
        r.IsInput = EditIsInput;
        r.Role = EditRole;
        r.Rack = ParseInt(EditRack, r.Rack, "架号");
        r.Level = ParseInt(EditLevel, r.Level, "层号");
        r.FunctionName = EditFunction;
        SelectedBinding.RefreshDisplay();

        // 保持原值时只提示一次（保存前校验还会兜底拦最后一道）
        static int ParseInt(string text, int fallback, string what) =>
            int.TryParse(text, out var v) ? v : fallback;
    }

    private void DeleteBinding()
    {
        if (SelectedBinding == null || SelectedDevice == null)
            return;
        SelectedDevice.Bindings.Remove(SelectedBinding.Raw);
        int index = Bindings.IndexOf(SelectedBinding);
        Bindings.Remove(SelectedBinding);
        SelectedBinding = Bindings.Count > 0
            ? Bindings[Math.Clamp(index, 0, Bindings.Count - 1)]
            : null;
        SyncEditorFromSelected();
    }

    /// <summary>"测试连接"：后台 Task 直测（后台线程网络 IO 铁律），完成弹结果。</summary>
    private async void TestConnection()
    {
        SyncDeviceFromEditor();
        if (SelectedDevice == null)
        {
            _dialogs.ShowMessage("先添加设备再测试。", "测试连接");
            return;
        }
        var device = SelectedDevice;
        var (ok, msg) = await Task.Run(() => IoCouplerConnectionTester.Test(device));
        if (ok)
            _dialogs.ShowMessage(msg, "测试连接");
        else
            _dialogs.ShowMessage(msg, "测试连接");
    }

    /// <summary>保存：先全量校验（错弹框不落盘），再写 pointtable.json。</summary>
    private void Save()
    {
        FlushSelectedToRaw();
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

    /// <summary>编辑面板未"应用"直接点保存时，把编辑值刷进选中行再校验（防丢）。</summary>
    private void FlushSelectedToRaw()
    {
        if (SelectedBinding?.Raw == null)
            return;
        ApplyBinding();
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
    }

    /// <summary>合成当前 VM 状态到配置对象（保存/校验共用）。</summary>
    private PointTableConfig BuildConfig() => new() { Devices = [.. Devices] };
}
