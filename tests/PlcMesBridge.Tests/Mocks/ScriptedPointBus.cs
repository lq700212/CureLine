// =========================================================================
// ScriptedPointBus：状态机测试用的总线桩（IPointTableBus 手动驱动版）
//
// 与 MockPointTableBus（自动演示流转）不同：本桩完全由测试脚本驱动——
//   SetInput 设快照并发边沿、SetSnapshotSilently 只改快照不发边沿（测
//   重启对齐/兜底路径）、Writes 录制全部灯写、WriteSucceeds 控制写失败。
// =========================================================================

using PlcMesBridge.Core.Comms;

namespace PlcMesBridge.Tests.Mocks;

public sealed class ScriptedPointBus : IPointTableBus
{
    public bool IsConnected { get; private set; }
    public string ConnectionSummary { get; private set; } = "桩";

    public PointTableSnapshot LatestSnapshot { get; private set; } = PointTableSnapshot.Empty;

    public event EventHandler<PointChangedEventArgs>? PointChanged;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Error;

    /// <summary>WritePoint 录制（成功才录）。</summary>
    public List<(IoPointKey Key, bool State)> Writes { get; } = new();

    /// <summary>下次 WritePoint 是否成功（false=模拟通讯失败，测补写自愈）。</summary>
    public bool WriteSucceeds { get; set; } = true;

    public void Start(PointTableConfig config)
    {
        IsConnected = true;
        ConnectionSummary = "桩（已启动）";
    }

    public void Stop() => IsConnected = false;

    public void Dispose() => Stop();

    public bool WritePoint(IoPointKey key, bool state)
    {
        if (!WriteSucceeds)
            return false;
        Writes.Add((key, state));
        SetSnapshotSilently(key, state); // 写了就进快照（模拟真机写后读回）
        return true;
    }

    /// <summary>设快照并发出变化边沿（一步到位的常规驱动）。</summary>
    public void SetInput(IoPointKey key, bool state)
    {
        SetSnapshotSilently(key, state);
        PointChanged?.Invoke(this, new PointChangedEventArgs(key, state));
    }

    /// <summary>只改快照不发边沿（测重启对齐/心跳兜底路径）。</summary>
    public void SetSnapshotSilently(IoPointKey key, bool state)
    {
        var states = new Dictionary<IoPointKey, bool>(LatestSnapshot.States) { [key] = state };
        LatestSnapshot = new PointTableSnapshot(states, DateTimeOffset.Now);
    }

    /// <summary>手动触发连接态边沿（测断连恢复对齐用）。</summary>
    public void RaiseConnection(bool connected) =>
        ConnectionChanged?.Invoke(this, connected);

    /// <summary>手动触发通讯错误边沿（测黄牌路径用）。</summary>
    public void RaiseError(string message) =>
        Error?.Invoke(this, message);
}
