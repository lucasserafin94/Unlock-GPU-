using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Periodic GPU reading on a background thread (<see cref="PeriodicTimer"/>), keeping ~30 minutes of history.
/// Raises <see cref="DriverReset"/> when the GPU library had to re-initialise (<see cref="IGpuInitGeneration"/>), when
/// readings recover after failing, or when a clock offset reads back as 0 after being non-zero (unless the app itself
/// set it to 0, as recorded by <see cref="OffsetTracker"/>). Events are raised on the background thread.
/// </summary>
public sealed class GpuMonitor : IGpuMonitor
{
    /// <summary>History length kept in memory.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);
    /// <summary>Minimum time between two <see cref="DriverReset"/> events.</summary>
    public static readonly TimeSpan ResetDebounce = TimeSpan.FromSeconds(10);
    const int MaxHistory = 7200;
    static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    readonly IGpu _gpu;
    readonly ILog _log;
    readonly OffsetTracker? _tracker;
    readonly TimeProvider _time;
    readonly Lock _gate = new();
    readonly Queue<GpuSample> _history = new();

    TimeSpan _interval = TimeSpan.FromSeconds(2);
    GpuSample? _latest;
    CancellationTokenSource? _cts;
    PeriodicTimer? _timer;
    Task? _loop;
    bool _disposed;
    int _pollThreadId;

    // reset detection (only touched by PollOnce, serialised by _pollGate)
    readonly Lock _pollGate = new();
    bool? _lastOk;
    int? _lastGeneration;
    int _lastCore;
    int _lastMem;
    DateTimeOffset _lastResetAt = DateTimeOffset.MinValue;

    /// <summary>Creates a stopped monitor.</summary>
    /// <param name="gpu">GPU access.</param>
    /// <param name="log">Log.</param>
    /// <param name="tracker">Offsets confirmed by the app (optional; avoids false resets when the user sets 0).</param>
    /// <param name="time">Clock for the reset debounce (tests).</param>
    public GpuMonitor(IGpu gpu, ILog log, OffsetTracker? tracker = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(log);
        _gpu = gpu;
        _log = log;
        _tracker = tracker;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public TimeSpan Interval
    {
        get { lock (_gate) return _interval; }
        set
        {
            if (value < MinInterval || value > MaxInterval) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                _interval = value;
                if (_timer != null) _timer.Period = value;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<GpuSample> History { get { lock (_gate) return _history.ToArray(); } }

    /// <inheritdoc/>
    public GpuSample? Latest { get { lock (_gate) return _latest; } }

    /// <inheritdoc/>
    public event EventHandler<GpuSample>? SampleArrived;

    /// <inheritdoc/>
    public event EventHandler? DriverReset;

    /// <inheritdoc/>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(_interval);
            var timer = _timer;
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(timer, ct), CancellationToken.None);
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            cts = _cts;
            _timer?.Dispose();
            _timer = null;
            _loop = null;
            _cts = null;
        }
        if (loop == null) return;
        cts!.Cancel();
        // A handler calling Stop from the monitor thread must not wait for itself.
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _pollThreadId))
        {
            try { loop.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException e) { _log.Error("MONITOR stop", e.InnerException); }
        }
        cts.Dispose();
    }

    /// <summary>Takes one reading synchronously, updates history and raises events. Used by the loop (and tests).</summary>
    public GpuSample PollOnce()
    {
        GpuSample sample;
        string? resetWhy;
        lock (_pollGate)
        {
            sample = _gpu.Sample();
            resetWhy = DetectReset(sample);
            lock (_gate)
            {
                _latest = sample;
                _history.Enqueue(sample);
                while (_history.Count > MaxHistory || (_history.Count > 1 && sample.Time - _history.Peek().Time > HistoryWindow))
                    _history.Dequeue();
            }
        }
        Raise(() => SampleArrived?.Invoke(this, sample));
        if (resetWhy != null)
        {
            _log.Warn("DRIVER RESET detected: " + resetWhy);
            Raise(() => DriverReset?.Invoke(this, EventArgs.Empty));
        }
        return sample;
    }

    string? DetectReset(GpuSample sample)
    {
        string? why = null;
        if (_gpu is IGpuInitGeneration g)
        {
            int gen = g.InitGeneration;
            if (_lastGeneration is int prev && gen != prev) why = "GPU library re-initialised";
            _lastGeneration = gen;
        }
        if (_lastOk == false && sample.Ok) why ??= "readings recovered after failing";
        _lastOk = sample.Ok;

        if (sample.Ok)
        {
            if (_gpu.GetOffset(ClockDomain.Core)?.CurrentMHz is int core)
            {
                if (_lastCore != 0 && core == 0 && _tracker?.CoreMHz is not 0) why ??= "core offset returned to 0";
                _lastCore = core;
            }
            if (_gpu.GetOffset(ClockDomain.Memory)?.CurrentMHz is int mem)
            {
                if (_lastMem != 0 && mem == 0 && _tracker?.MemMHz is not 0) why ??= "memory offset returned to 0";
                _lastMem = mem;
            }
        }

        if (why == null) return null;
        var now = _time.GetUtcNow();
        if (now - _lastResetAt < ResetDebounce) return null;
        _lastResetAt = now;
        return why;
    }

    async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            PollSafe();
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                PollSafe();
        }
        catch (OperationCanceledException) { }
    }

    void PollSafe()
    {
        Volatile.Write(ref _pollThreadId, Environment.CurrentManagedThreadId);
        try { PollOnce(); }
        catch (Exception e) when (e is not OperationCanceledException) { _log.Error("MONITOR poll", e); }
        finally { Volatile.Write(ref _pollThreadId, 0); }
    }

    void Raise(Action raise)
    {
        try { raise(); }
        catch (Exception e) { _log.Error("MONITOR event handler", e); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
