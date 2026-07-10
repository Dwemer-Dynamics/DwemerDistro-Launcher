using System.Windows.Input;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    public void Execute(object? parameter)
    {
        _execute();
    }

    public void RaiseCanExecuteChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }
}
