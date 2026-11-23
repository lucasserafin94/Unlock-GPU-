using System.Globalization;
using UnlockGpu.Core.Bench;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;

namespace UnlockGpu.Core.Services;

/// <summary>Localizer keys used as <see cref="TuneResult.StopReasonKey"/> / <see cref="TuneStep.StopReasonKey"/>.</summary>
public static class TuneStopReasons
{
    /// <summary>Reached the driver maximum or <see cref="TuneOptions.MaxOffsetMHz"/>.</summary>
    public const string MaxOffset = "tune.stop.maxOffset";
    /// <summary>Two steps in a row gained less than <see cref="TuneOptions.MinGainPercent"/>.</summary>
    public const string NoGain = "tune.stop.noGain";
    /// <summary>The score fell below the best by more than <see cref="TuneOptions.MinGainPercent"/>.</summary>
    public const string ScoreDrop = "tune.stop.scoreDrop";
    /// <summary>The VRAM test found corrupted words.</summary>
    public const string VramErrors = "tune.stop.vramErrors";
    /// <summary>The GPU reached <see cref="TuneOptions.MaxTempC"/>.</summary>
    public const string Temperature = "tune.stop.temperature";
    /// <summary>The driver reset / device was removed while testing.</summary>
    public const string DeviceRemoved = "tune.stop.deviceRemoved";
    /// <summary>The user cancelled.</summary>
    public const string Cancelled = "tune.stop.cancelled";
    /// <summary>Applying an offset failed or did not read back.</summary>
    public const string ApplyFailed = "tune.stop.applyFailed";
    /// <summary>The benchmark or VRAM test could not run.</summary>
    public const string BenchFailed = "tune.stop.benchFailed";
    /// <summary>Clock offsets are not readable on this GPU/driver.</summary>
    public const string Unavailable = "tune.stop.unavailable";
    /// <summary>Unexpected error (see log).</summary>
    public const string Error = "tune.stop.error";
    /// <summary>The run stopped early and the original offset could not be put back (a test offset may still be applied).</summary>
    public const string RestoreFailed = "tune.stop.restoreFailed";
}

/// <summary>Automatic offset search for one clock domain.
/// <list type="bullet">
/// <item>Measures a baseline at the current offset, then raises it by <see cref="TuneOptions.StepMHz"/> up to
/// <see cref="TuneOptions.MaxOffsetMHz"/> (clamped to the driver maximum), benchmarking each step and, for the memory
/// target, running the VRAM test on half of the free memory.</item>
/// <item>A step only becomes the new best when it beats the best score by at least <see cref="TuneOptions.MinGainPercent"/>
/// (the lowest offset that gives the gain is preferred). It stops after two steps in a row without that gain, on a drop
/// of more than that margin, on VRAM errors or at <see cref="TuneOptions.MaxTempC"/> (polled during each measurement);
/// these are normal stops and the best offset is then applied.</item>
/// <item>On cancellation, device removal, benchmark/apply failure or any exception the original offset is restored
/// (in a <c>finally</c>, retried once) and nothing else is kept; if it still cannot be restored the result carries
/// <see cref="TuneStopReasons.RestoreFailed"/>. Cancellation is reported as a result with
/// <see cref="TuneStopReasons.Cancelled"/>, not as an exception, so the UI can still show the measured steps.</item>
/// </list>
/// The other clock domain is re-applied with its current value on every change. Not thread-safe: run one tuning at a time.</summary>
public sealed class AutoTuner : IAutoTuner
{
    /// <summary>Fraction of free VRAM used by the per-step VRAM test.</summary>
    public const double VramTestFraction = 0.5;
    /// <summary>How many times the original offset is re-applied when a run ends early.</summary>
    public const int RestoreAttempts = 2;

    readonly IGpu _gpu;
    readonly IOffsetService _offsets;
    readonly IGpuBenchmark _benchmark;
    readonly IVramTester _vramTester;
    readonly ILog _log;

    readonly WriteGate? _writeGate;

    public AutoTuner(IGpu gpu, IOffsetService offsets, IGpuBenchmark benchmark, IVramTester vramTester, ILog log, WriteGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(vramTester);
        ArgumentNullException.ThrowIfNull(log);
        _gpu = gpu; _offsets = offsets; _benchmark = benchmark; _vramTester = vramTester; _log = log; _writeGate = gate;
    }

    /// <summary>How often the temperature is read while a measurement runs.</summary>
    public TimeSpan TemperaturePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    sealed record Measurement(double Score, long VramErrors, string? StopKey, bool Failed);

    /// <inheritdoc />
    public async Task<TuneResult> RunAsync(TuneOptions options, IProgress<TuneStep>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StepMHz, 0, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SampleDuration, TimeSpan.Zero, nameof(options));
        using var exclusive = _writeGate?.EnterTuner();

        var steps = new List<TuneStep>();
        var domain = options.Target == TuneTarget.Core ? ClockDomain.Core : ClockDomain.Memory;
        var info = _offsets.Get(domain);
        var other = _offsets.Get(domain == ClockDomain.Core ? ClockDomain.Memory : ClockDomain.Core);
        if (info == null || other == null)
        {
            _log.Warn("AutoTuner: clock offsets not readable; nothing done.");
            return new TuneResult(0, 0, 0, steps, TuneStopReasons.Unavailable);
        }

        int original = info.CurrentMHz;
        int limit = Math.Min(options.MaxOffsetMHz ?? info.MaxMHz, info.MaxMHz);
        int best = original, current = original;
        double baseline = 0, bestScore = 0;
        bool changed = false, finished = false;
        string stop = TuneStopReasons.Error;
        _log.Info(Invariant($"AutoTuner {options.Target}: start at {original} MHz, step {options.StepMHz}, limit {limit}"));

        void Report(TuneStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        ApplyResult Apply(int offset) => domain == ClockDomain.Core
            ? _offsets.Apply(offset, other.CurrentMHz)
            : _offsets.Apply(other.CurrentMHz, offset);

        try
        {
            if (IsHot(options.MaxTempC))
            {
                stop = TuneStopReasons.Temperature;
                finished = true;
            }
            else
            {
                var m0 = await MeasureAsync(options, ct).ConfigureAwait(false);
                baseline = bestScore = m0.Score;
                string? baseStop = m0.Failed ? m0.StopKey : m0.VramErrors > 0 ? TuneStopReasons.VramErrors : m0.StopKey;
                Report(new TuneStep(original, m0.Score, null, m0.VramErrors, baseStop));
                if (baseStop != null)
                {
                    stop = baseStop;
                    finished = !m0.Failed || baseStop == TuneStopReasons.Temperature;
                }
                else (stop, finished) = await ClimbAsync().ConfigureAwait(false);
            }

            if (finished && changed && current != best)
            {
                var r = Apply(best);
                if (r.Ok) current = best;
                else
                {
                    _log.Warn($"AutoTuner: applying best {best} MHz failed: {r.ErrorText}");
                    stop = TuneStopReasons.ApplyFailed;
                    finished = false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stop = TuneStopReasons.Cancelled;
            finished = false;
        }
        catch (Exception e)
        {
            _log.Error("AutoTuner failed", e);
            stop = TuneStopReasons.Error;
            finished = false;
        }
        finally
        {
            if (!finished && changed && !RestoreOriginal()) stop = TuneStopReasons.RestoreFailed;
        }

        int final = finished ? best : original;
        _log.Info(Invariant($"AutoTuner {options.Target}: {stop}, offset {final} MHz, baseline {baseline:F1}, best {bestScore:F1}"));
        return new TuneResult(final, baseline, finished ? bestScore : baseline, steps, stop);

        // Puts the original offset back. Never throws: it runs in the finally block and must not hide the result.
        // A second attempt follows a GPU reading, which re-opens the GPU library after a driver reset (stale handle).
        bool RestoreOriginal()
        {
            for (int attempt = 1; attempt <= RestoreAttempts; attempt++)
            {
                try
                {
                    var r = Apply(original);
                    if (r.Ok)
                    {
                        _log.Info(Invariant($"AutoTuner: restored original offset {original} MHz"));
                        return true;
                    }
                    _log.Warn(Invariant($"AutoTuner: restore attempt {attempt} of {original} MHz failed: {r.ErrorText}"));
                }
                catch (Exception e)
                {
                    _log.Error(Invariant($"AutoTuner: restore attempt {attempt} of {original} MHz threw"), e);
                }
                if (attempt < RestoreAttempts) RefreshGpu();
            }
            _log.Error(Invariant($"AutoTuner: could not restore original offset {original} MHz; a test offset may still be applied"));
            return false;
        }

        async Task<(string Stop, bool Finished)> ClimbAsync()
        {
            int lowGain = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int next = Math.Min(current + options.StepMHz, limit);
                if (next <= current) return (TuneStopReasons.MaxOffset, true);

                changed = true;
                var applied = Apply(next);
                if (!applied.Ok)
                {
                    _log.Warn($"AutoTuner: apply {next} MHz failed: {applied.ErrorText}");
                    Report(new TuneStep(next, 0, null, 0, TuneStopReasons.ApplyFailed));
                    return (TuneStopReasons.ApplyFailed, false);
                }
                current = next;

                var m = await MeasureAsync(options, ct).ConfigureAwait(false);
                if (m.Failed)
                {
                    Report(new TuneStep(current, m.Score, null, m.VramErrors, m.StopKey));
                    return (m.StopKey!, m.StopKey == TuneStopReasons.Temperature);
                }

                double gain = bestScore > 0 ? (m.Score - bestScore) / bestScore * 100.0 : 0;
                string? stepStop = null;
                if (m.VramErrors > 0) stepStop = TuneStopReasons.VramErrors;
                else if (gain < -options.MinGainPercent) stepStop = TuneStopReasons.ScoreDrop;
                else if (gain >= options.MinGainPercent) { best = current; bestScore = m.Score; lowGain = 0; }
                else if (++lowGain >= 2) stepStop = TuneStopReasons.NoGain;
                stepStop ??= m.StopKey; // temperature reached right after a valid measurement

                Report(new TuneStep(current, m.Score, gain, m.VramErrors, stepStop));
                if (stepStop != null) return (stepStop, true);
            }
        }
    }

    /// <summary>Benchmark (+ VRAM test for the memory target) with a temperature watchdog that aborts the run when hot.</summary>
    async Task<Measurement> MeasureAsync(TuneOptions options, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchTemperatureAsync(options.MaxTempC, linked);
        try
        {
            var bench = await _benchmark.RunAsync(options.SampleDuration, linked.Token).ConfigureAwait(false);
            if (!bench.Completed) return Failure(bench.ErrorKey);

            long vramErrors = 0;
            if (options.Target == TuneTarget.Memory && options.RunVramTest)
            {
                var vram = await _vramTester.RunAsync(VramTestFraction, linked.Token).ConfigureAwait(false);
                if (!vram.Completed) return Failure(vram.ErrorKey) with { Score = bench.Score };
                vramErrors = vram.Errors;
            }
            bool hot = await StopWatcherAsync().ConfigureAwait(false);
            return new Measurement(bench.Score, vramErrors, hot ? TuneStopReasons.Temperature : null, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the watchdog cancels the linked token without the caller's token being cancelled.
            bool hot = await StopWatcherAsync().ConfigureAwait(false);
            return new Measurement(0, 0, hot ? TuneStopReasons.Temperature : TuneStopReasons.BenchFailed, true);
        }
        finally
        {
            await StopWatcherAsync().ConfigureAwait(false);
        }

        async Task<bool> StopWatcherAsync()
        {
            if (!watcher.IsCompleted) await linked.CancelAsync().ConfigureAwait(false);
            return await watcher.ConfigureAwait(false);
        }

        static Measurement Failure(string? errorKey) => new(0, 0,
            errorKey == BenchErrorKeys.DeviceRemoved ? TuneStopReasons.DeviceRemoved : TuneStopReasons.BenchFailed, true);
    }

    /// <summary>Returns true (and cancels <paramref name="cts"/>) when the GPU gets hot; false when stopped by cancellation.</summary>
    async Task<bool> WatchTemperatureAsync(uint maxTempC, CancellationTokenSource cts)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TemperaturePollInterval, cts.Token).ConfigureAwait(false);
                if (IsHot(maxTempC))
                {
                    _log.Warn(Invariant($"AutoTuner: temperature reached {maxTempC} C, stopping the measurement"));
                    await cts.CancelAsync().ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Takes one GPU reading so the GPU library re-opens a stale handle after a driver reset.</summary>
    void RefreshGpu()
    {
        try { _ = _gpu.Sample(); }
        catch (Exception e) { _log.Error("AutoTuner: GPU reading before restore failed", e); }
    }

    bool IsHot(uint maxTempC)
    {
        try
        {
            var s = _gpu.Sample();
            return s.Ok && s.TempC >= maxTempC;
        }
        catch (Exception e)
        {
            _log.Error("AutoTuner: temperature read failed", e);
            return false;
        }
    }

    static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
