using System.Windows.Input;

namespace UnlockGpu.App.Mvvm;

/// <summary>Synchronous command. <see cref="CanExecuteChanged"/> follows WPF's requery plus explicit raises.</summary>
public sealed class RelayCommand : ICommand
{
    readonly Action<object?> _execute;
    readonly Func<object?, bool>? _canExecute;
    EventHandler? _explicit;

    /// <summary>Command without a parameter.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        _canExecute = canExecute == null ? null : _ => canExecute();
    }

    /// <summary>Command that receives the binding's CommandParameter.</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; _explicit += value; }
        remove { CommandManager.RequerySuggested -= value; _explicit -= value; }
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    /// <summary>Asks bound controls to query <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged() => _explicit?.Invoke(this, EventArgs.Empty);
}
