// =========================================================================
// ScanNoticeViewModel：扫码拦截提示弹窗的编排（V0.0.99 新增）
//
// 干什么：扫码流程被拦截时（上一个绑定流程未结束 / 禁放回原库位 / 禁混装 /
//   禁往在制库位加新产品），弹一个窗把"为什么拦、下一步怎么做"讲清楚。
//   纯告知型：一个【知道了】按钮，没有决策——所以 VM 只有一个关闭命令。
// 为什么要有这个窗（而不是只在状态条闪一行红字）：这几条都是**现场红线**
//   拦截，操作员必须停下来看清楚为什么没扫进去；状态条 3 秒浮层一闪而过，
//   操作员很可能没看清就继续扫，扫了又不通，效率反而更低。
// 线程：UI 线程（弹窗是模态）。
// =========================================================================

using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>扫码提示弹窗 VM（纯告知，一个"知道了"按钮）。</summary>
public sealed class ScanNoticeViewModel : ViewModelBase
{
    /// <summary>弹窗标题（如"扫码被拦截"）。</summary>
    public string Title { get; }

    /// <summary>主提示正文（一句话说清发生了什么）。</summary>
    public string Message { get; }

    /// <summary>操作指引正文（说清下一步怎么做；可为空）。</summary>
    public string Detail { get; }

    /// <summary>关闭命令（点了就收窗）。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>构造（文案由调用方给；close=收窗回调）。</summary>
    public ScanNoticeViewModel(string title, string message, string detail, Action close)
    {
        Title = title;
        Message = message;
        Detail = detail ?? "";
        CloseCommand = new RelayCommand(() => close());
    }
}
