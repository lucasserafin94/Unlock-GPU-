using System.Globalization;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Memory clock lock through NVML (nvmlDeviceSetMemoryLockedClocks / Reset). What the app applied is kept in a state
/// file valid for the current boot only. The real state is detected from the GPU itself: an idle GPU without a lock
/// drops to P4/P5/P8; staying in P0/P2 at low utilisation for 3 consecutive samples means locked. Right after locking
/// there is a 10 s grace period while the GPU ramps up. Same rules as the reference PowerShell app.
/// Note for the UI (<see cref="ServiceTextKeys.VramPowerCapNote"/>): locking lowers total power when the GPU is power-capped.
/// </summary>
public sealed class VramLockService : IVramLockService
{
    /// <summary>Consecutive idle samples in a high P-state needed to confirm a lock.</summary>
    public const int ConfirmSamples = 3;
    /// <summary>Utilisation (%) below which the GPU counts as idle.</summary>
    public const uint IdleUtilPercent = 15;
    /// <summary>Time after locking during which an idle low P-state does not yet count as "lost".</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    readonly IGpu _gpu;
    readonly ILog _log;
    readonly string _stateFile;
    readonly TimeProvider _time;
    readonly Func<DateTime> _bootTimeUtc;
    readonly Lock _gate = new();

    int _idleLockedSeen;
    Detection? _lastDetected;
    DateTimeOffset? _lockedAt;

    /// <summary>Creates the service.</summary>
    /// <param name="gpu">GPU access.</param>
    /// <param name="paths">App folders (state file <c>vram-lock.json</c>).</param>
    /// <param name="log">Log.</param>
    /// <param name="time">Clock for the grace period (tests); defaults to the system clock.</param>
    /// <param name="bootTimeUtc">Boot identity (tests); defaults to <see cref="BootClock.BootTimeUtc"/>.</param>
    readonly WriteGate? _writeGate;

    public VramLockService(IGpu gpu, AppPaths paths, ILog log, TimeProvider? time = null, Func<DateTime>? bootTimeUtc = null, WriteGate? gate = null)
    {
        _writeGate = gate;
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _gpu = gpu;
        _log = log;
        _stateFile = Path.Combine(paths.DataDir, "vram-lock.json");
        _time = time ?? TimeProvider.System;
        _bootTimeUtc = bootTimeUtc ?? BootClock.BootTimeUtc;
    }

    /// <inheritdoc/>
    public IReadOnlyList<uint> Levels =>
        _gpu.Info?.MemClocks.Distinct().OrderByDescending(x => x).Take(2).ToArray() ?? [];

    /// <inheritdoc/>
    public ApplyResult Lock(uint levelMHz)
    {
        if (_writeGate?.IsBlocked == true) return ApplyError.Fail(levelMHz.ToString(System.Globalization.CultureInfo.InvariantCulture) + " MHz", ServiceTextKeys.TunerBusy);
        string requested = Mhz(levelMHz);
        var levels = Levels;
        if (levels.Count == 0) return ApplyError.Fail(requested, ServiceTextKeys.VramNoLevels, _gpu.LastError);
        if (!levels.Contains(levelMHz))
            return ApplyError.Fail(requested, ServiceTextKeys.VramInvalidLevel, string.Join("/", levels.Select(Mhz)));

        lock (_gate)
        {
            int rc = _gpu.LockMemoryClock(levelMHz);
            if (rc != 0)
            {
                string err = _gpu.ErrorText(rc);
                _log.Warn($"LOCK {requested} refused: {err}");
                return ApplyError.Fail(requested, ServiceTextKeys.VramDriver, err);
            }
            SaveState(true, levelMHz);
            _lastDetected = new Detection(levelMHz, null);
            _lockedAt = _time.GetUtcNow();
            var s = _gpu.Sample();
            string readBack = s.Ok ? string.Create(CultureInfo.InvariantCulture, $"{s.MemMHz} MHz ({s.PState})") : "?";
            _log.Info($"LOCK {requested} ok, now {readBack}");
            return new ApplyResult(true, requested, readBack);
        }
    }

    /// <inheritdoc/>
    public ApplyResult Unlock()
    {
        if (_writeGate?.IsBlocked == true) return ApplyError.Fail("unlock", ServiceTextKeys.TunerBusy);
        const string requested = "auto";
        lock (_gate)
        {
            int rc = _gpu.ResetMemoryClock();
            if (rc != 0)
            {
                string err = _gpu.ErrorText(rc);
                _log.Warn($"UNLOCK refused: {err}");
                return ApplyError.Fail(requested, ServiceTextKeys.VramDriver, err);
            }
            SaveState(false, null);
            _lastDetected = null;
            _idleLockedSeen = 0;
            _lockedAt = null;
            _log.Info("UNLOCK ok");
            return new ApplyResult(true, requested, "auto");
        }
    }

    /// <inheritdoc/>
    public VramLockState Evaluate(GpuSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            var st = LoadState();
            bool stLocked = st is { Locked: true, ClockMHz: not null };
            bool valid = sample.Ok;

            if (valid && sample.UtilGpu < IdleUtilPercent)
            {
                uint? baseMHz = BaseFor(sample.PState, Levels);
                if (baseMHz is uint b)
                {
                    _idleLockedSeen++;
                    if (_idleLockedSeen >= ConfirmSamples || stLocked)
                    {
                        int offset = (int)sample.MemMHz - (int)b;
                        _lastDetected = new Detection(b, offset);
                        return new VramLockState(true, b, offset, false);
                    }
                    return _lastDetected is { } d
                        ? new VramLockState(true, d.BaseMHz, d.OffsetMHz, false)
                        : new VramLockState(false, null, null, false);
                }
                if (_lockedAt is { } at && _time.GetUtcNow() - at < Grace && _lastDetected is { } g)
                    return new VramLockState(true, g.BaseMHz, null, false);

                _idleLockedSeen = 0;
                _lastDetected = null;
                return new VramLockState(false, null, null, stLocked);
            }

            if (_lastDetected is { } last) return new VramLockState(true, last.BaseMHz, last.OffsetMHz, false);
            if (stLocked)
            {
                uint clock = st!.ClockMHz!.Value;
                int? offset = valid ? (int)sample.MemMHz - (int)clock : null;
                return new VramLockState(true, clock, offset, false);
            }
            return new VramLockState(false, null, null, false);
        }
    }

    /// <summary>Lock level that corresponds to a high P-state: P0 = highest level, P2 = second level.</summary>
    static uint? BaseFor(string pstate, IReadOnlyList<uint> levels)
    {
        if (levels.Count == 0) return null;
        if (pstate == "P0") return levels[0];
        if (pstate == "P2" && levels.Count >= 2) return levels[1];
        return null;
    }

    LockStateFile? LoadState()
    {
        var st = JsonStore.Load<LockStateFile>(_stateFile);
        return st != null && BootClock.SameBoot(st.BootUtc, _bootTimeUtc()) ? st : null;
    }

    void SaveState(bool locked, uint? clock)
    {
        try { JsonStore.Save(_stateFile, new LockStateFile(locked, clock, "app", _bootTimeUtc(), _time.GetUtcNow().UtcDateTime)); }
        catch (IOException e) { _log.Error("LOCK state file", e); }
        catch (UnauthorizedAccessException e) { _log.Error("LOCK state file", e); }
    }

    static string Mhz(uint v) => v.ToString(CultureInfo.InvariantCulture) + " MHz";

    readonly record struct Detection(uint BaseMHz, int? OffsetMHz);

    /// <summary>What the app applied, valid for one boot.</summary>
    internal sealed record LockStateFile(bool Locked, uint? ClockMHz, string Source, DateTime BootUtc, DateTime WhenUtc);
}
