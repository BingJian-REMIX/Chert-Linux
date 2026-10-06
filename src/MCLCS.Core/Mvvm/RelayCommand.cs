using System.Windows.Input;

namespace MCLCS.Core.Mvvm;

/// <summary>
/// 命令执行异常的全局上报钩子。
/// 命令由 UI 直接调用，<c>ICommand.Execute</c> 没有返回值、异常无处传播：
/// 同步命令冒到 Dispatcher，异步命令经 <c>async void</c> 冒到
/// <c>AppDomain.UnhandledException</c> —— 后者一律弹「启动器崩溃」，
/// 于是「没装 Java」这类可预期的业务失败也被报成崩溃。
/// UI 层启动时挂上 Reporter（写日志 + Toast），命令里未捕获的异常就降级成一条提示。
/// </summary>
public static class CommandErrors
{
    /// <summary>上报实现（由 UI 层注入；未注入时仅调试输出）。</summary>
    public static Action<Exception>? Reporter { get; set; }

    /// <summary>上报一条命令异常。上报本身绝不再抛，避免级联崩溃。</summary>
    public static void Report(Exception ex)
    {
        try
        {
            if (Reporter is not null) Reporter(ex);
            else System.Diagnostics.Debug.WriteLine($"[CommandError] {ex}");
        }
        catch
        {
            // 上报失败也不得冒泡
        }
    }
}

/// <summary>简单 ICommand 实现（同步）。</summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try { _execute(parameter); }
        catch (Exception ex) { CommandErrors.Report(ex); }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>异步 ICommand 实现（基于 Task）。</summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _isRunning = true;
        RaiseCanExecuteChanged();
        try { await _execute(parameter); }
        catch (OperationCanceledException) { /* 用户取消：无需提示 */ }
        catch (Exception ex) { CommandErrors.Report(ex); }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
