using System.Windows.Threading;

namespace UnlockGpu.App.Services;

/// <summary>Marshals work to the UI thread (monitor events arrive on a background thread).</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting.</summary>
    void Post(Action action);
}

/// <summary><see cref="IUiDispatcher"/> over a WPF <see cref="Dispatcher"/>.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    /// <inheritdoc/>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
