namespace UnlockGpu.Core.Services;

/// <summary>
/// Exclusive write access to the GPU while the auto-tuner runs. The tuner enters the gate; any other write
/// (Tuning tab, tray profile, reset, driver-reset reapply) that is not on the tuner's own async flow is refused
/// with <see cref="ServiceTextKeys.TunerBusy"/> until the tuner finishes and has restored or applied its result.
/// One instance is shared by every writing service.
/// </summary>
public sealed class WriteGate
{
    readonly AsyncLocal<bool> _tunerFlow = new();
    int _tunerActive;

    /// <summary>True while an auto-tune run holds the gate.</summary>
    public bool TunerActive => Volatile.Read(ref _tunerActive) == 1;

    /// <summary>True when the current caller must not write (tuner active and caller is not the tuner).</summary>
    public bool IsBlocked => TunerActive && !_tunerFlow.Value;

    /// <summary>Called by the tuner at the start of its run (synchronously, so the flag flows to its awaits).</summary>
    public IDisposable EnterTuner()
    {
        if (Interlocked.CompareExchange(ref _tunerActive, 1, 0) != 0)
            throw new InvalidOperationException(ApplyError.Compose(ServiceTextKeys.TunerBusy));
        _tunerFlow.Value = true;
        return new Scope(this);
    }

    sealed class Scope(WriteGate gate) : IDisposable
    {
        int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            gate._tunerFlow.Value = false;
            Volatile.Write(ref gate._tunerActive, 0);
        }
    }
}
