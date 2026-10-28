// =========================================================================
// RelayCommand：ICommand 通用实现（按钮/菜单绑定的命令）
//
// 干什么：XAML 的 Button.Command / MenuItem.Command 只能绑 ICommand，
//   本类把"执行方法 + 可执行判断"包成命令，VM 里只暴露属性。
// 怎么改：需要参数用 RelayCommand<T>（CommandParameter 传参）；
//   可执行条件变了调 RaiseCanExecuteChanged（WPF 自动刷新按钮灰态）。
// =========================================================================

using System.Windows.Input;

namespace PlcMesBridge.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>简化版（无参数命令，VM 里最常用）。</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(),
            canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>可执行条件变了调它（按钮自动变灰/恢复）。</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke((T?)parameter) ?? true;

    public void Execute(object? parameter) => _execute((T?)parameter);

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
