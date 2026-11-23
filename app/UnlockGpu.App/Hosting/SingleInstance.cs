namespace UnlockGpu.App.Hosting;

/// <summary>
/// One interactive instance per user session: a named mutex marks the first instance; a later launch signals a named
/// event so the first instance brings its window to the front, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\UnlockGPU.SingleInstance";
    const string EventName = @"Local\UnlockGPU.Activate";

    readonly Mutex _mutex;
    readonly EventWaitHandle _activate;
    readonly RegisteredWaitHandle _wait;
    bool _disposed;

    SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Returns the instance guard when this is the first instance; otherwise signals the first one and returns null.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        bool owned = createdNew;
        if (!owned)
        {
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
        }
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (owned) return new SingleInstance(mutex, activate);

        activate.Set();
        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
