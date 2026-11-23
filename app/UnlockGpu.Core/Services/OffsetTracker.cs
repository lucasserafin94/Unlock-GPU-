namespace UnlockGpu.Core.Services;

/// <summary>
/// Thread-safe record of the clock offsets the app last read back after applying them.
/// Shared by <see cref="OffsetService"/> (writer) and <see cref="GpuMonitor"/> (reader) so that an offset the user
/// deliberately set to 0 is not mistaken for a driver reset. One instance per app (registered as a singleton).
/// </summary>
public sealed class OffsetTracker
{
    readonly Lock _gate = new();
    int? _core;
    int? _mem;

    /// <summary>Core offset last confirmed by the app, or null when the app never applied one.</summary>
    public int? CoreMHz { get { lock (_gate) return _core; } }

    /// <summary>Memory offset last confirmed by the app, or null when the app never applied one.</summary>
    public int? MemMHz { get { lock (_gate) return _mem; } }

    /// <summary>Records the values read back after an apply. Null keeps the previous value of that domain.</summary>
    public void Record(int? coreMHz, int? memMHz)
    {
        lock (_gate)
        {
            if (coreMHz is not null) _core = coreMHz;
            if (memMHz is not null) _mem = memMHz;
        }
    }
}
