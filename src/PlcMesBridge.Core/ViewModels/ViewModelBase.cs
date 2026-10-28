// =========================================================================
// ViewModelBase：全部 ViewModel 的基类（MVVM 的 V 与 VM 粘合剂）
//
// 干什么：INotifyPropertyChanged + 跨线程封送。后台线程（PLC 节拍/网关）
//   改 VM 属性时，通知自动 Post 回 UI 线程，界面不跨线程崩。
// 为什么捕获 SynchronizationContext：VM 构造在 UI 线程（窗体 new 时），
//   此时 Current 就是 UI 上下文；后台线程调 Set 时 Post 回去。
//   测试里 Current 为空（无 UI 线程），直接同步触发，不依赖 Dispatcher。
// 怎么改：属性一律 private 字段 + Set(ref, value)；语言切换整窗刷新调
//   RefreshAll()（Raise("") = 全部属性失效重读，见 LanguageService 注释）。
// =========================================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PlcMesBridge.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>UI 线程上下文（构造时捕获；测试无 UI 线程则为 null）。</summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>
    /// 字段赋值 + 变了才通知（返回 true 表示变了）。后台线程可直接调，
    /// 通知自动封送 UI 线程。
    /// </summary>
    protected bool Set<T>(ref T field, T value,
        [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>发通知（name 为空串表示全部属性重读，语言切换用）。</summary>
    protected void Raise([CallerMemberName] string? name = null)
    {
        // UI 线程直接发；后台线程 Post 回 UI（测试无上下文同步发）。
        if (_ui != null && SynchronizationContext.Current != _ui)
            _ui.Post(_ => PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(name)), null);
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>全部属性重读（语言切换后调，XAML 绑定文本全刷新）。</summary>
    protected void RefreshAll() => Raise(string.Empty);

    /// <summary>
    /// 回 UI 线程执行（后台任务更新 VM 状态统一走这里；测试无上下文同步执行）。
    /// </summary>
    protected void UiInvoke(Action action)
    {
        if (_ui != null && SynchronizationContext.Current != _ui)
            _ui.Post(_ => action(), null);
        else
            action();
    }
}
