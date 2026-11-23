namespace UnlockGpu.Core.Services;

/// <summary>
/// Exclusive write access to the GPU while the auto-tuner runs. The tuner enters the gate; any other write
/// (Tuning tab, tray profile, reset, driver-reset reapply) that is not on the tuner's own async flow is refused
/// with <see cref="ServiceTextKeys.TunerBusy"/> until the tuner finishes and has restored or applied its result.
/// Writers bracket their GPU write with <see cref="TryBeginWrite"/> / <see cref="EndWrite"/>, so a write that
/// already started when the tuner enters is waited for instead of racing the tuner's first write.
/// One instance is shared by every writing service.
/// </summary>
public sealed class WriteGate
{
    /// <summary>How long <see cref="EnterTuner"/> waits for in-flight writes before proceeding anyway.</summary>
    static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    readonly AsyncLocal<bool> _tunerFlow = new();
    int _tunerActive;
    int _inFlight;

    /// <summary>True while an auto-tune run holds the gate.</summary>
    public bool TunerActive => Volatile.Read(ref _tunerActive) == 1;

    /// <summary>True when the current caller must not write (tuner active and caller is not the tuner).</summary>
    public bool IsBlocked => TunerActive && !_tunerFlow.Value;

    /// <summary>Number of writes currently between <see cref="TryBeginWrite"/> and <see cref="EndWrite"/>.</summary>
    public int InFlightWrites => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Starts a GPU write. Returns false (and counts nothing) when the tuner holds the gate and the caller is not
    /// on the tuner's flow. On true the caller must call <see cref="EndWrite"/> in a finally block.
    /// </summary>
    public bool TryBeginWrite()
    {
        // Conta primeiro e so depois olha o flag: com o CAS do EnterTuner (barreira completa dos dois lados),
        // ou o tuner ve este contador, ou este escritor ve o tuner ativo. Nunca os dois passam batido.
        Interlocked.Increment(ref _inFlight);
        if (!IsBlocked) return true;
        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    /// <summary>Ends a write started by a successful <see cref="TryBeginWrite"/>.</summary>
    public void EndWrite()
    {
        if (Interlocked.Decrement(ref _inFlight) < 0)
            Interlocked.Increment(ref _inFlight);   // EndWrite sem TryBeginWrite: nao deixa o contador negativo
    }

    /// <summary>
    /// Called by the tuner at the start of its run (synchronously, so the flag flows to its awaits). After
    /// claiming the gate it waits (bounded) for writes already in flight on other flows to finish.
    /// </summary>
    public IDisposable EnterTuner()
    {
        if (Interlocked.CompareExchange(ref _tunerActive, 1, 0) != 0)
            throw new InvalidOperationException(ApplyError.Compose(ServiceTextKeys.TunerBusy));
        _tunerFlow.Value = true;
        // Escritas do proprio tuner so acontecem depois que este metodo retorna, entao aqui o contador so tem
        // escritas de outros fluxos. Espera limitada: se algo travar, segue em frente (a escrita presa nao e do tuner).
        if (Volatile.Read(ref _inFlight) > 0)
        {
            long deadline = Environment.TickCount64 + (long)DrainTimeout.TotalMilliseconds;
            var spin = new SpinWait();
            while (Volatile.Read(ref _inFlight) > 0 && Environment.TickCount64 < deadline)
                spin.SpinOnce();
        }
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
