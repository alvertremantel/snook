using System.Windows.Input;

namespace Snook.UI;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly bool _disableWhileRunning;
    private bool _isRunning;

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, bool disableWhileRunning = true)
    {
        _execute = execute;
        _canExecute = canExecute;
        _disableWhileRunning = disableWhileRunning;
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => (!_isRunning || !_disableWhileRunning) && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter)
    {
        if (_isRunning || !CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
