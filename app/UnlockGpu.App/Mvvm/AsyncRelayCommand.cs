using System.Windows.Input;

namespace UnlockGpu.App.Mvvm;

/// <summary>
/// Command that runs an asynchronous action. It is disabled while running (no re-entrance) and routes exceptions to
/// the supplied handler instead of letting them escape into the dispatcher.
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    readonly Func<object?, Task> _execute;
    readonly Func<object?, bool>? _canExecute;
    readonly Action<Exception>? _onError;
    EventHandler? _explicit;
    bool _isRunning;

    /// <summary>Command without a parameter.</summary>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        _canExecute = canExecute == null ? null : _ => canExecute();
        _onError = onError;
    }

    /// <summary>Command that receives the binding's CommandParameter.</summary>
    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <summary>True while the action is running.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value)) RaiseCanExecuteChanged();
        }
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; _explicit += value; }
        remove { CommandManager.RequerySuggested -= value; _explicit -= value; }
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc/>
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        await ExecuteAsync(parameter).ConfigureAwait(true);
    }

    /// <summary>Runs the action and completes when it finishes. Exceptions go to the error handler.</summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        IsRunning = true;
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome.
        }
        catch (Exception ex) when (_onError != null)
        {
            _onError(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Asks bound controls to query <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged() => _explicit?.Invoke(this, EventArgs.Empty);
}
