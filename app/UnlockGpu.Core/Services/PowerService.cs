using System.Globalization;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Power limit through NVML. <see cref="Probe"/> re-applies the limit currently in force once per driver version to
/// learn whether the limit is adjustable; only definitive answers (rc 0 = adjustable, rc 3 = NOT_SUPPORTED) are cached
/// in <c>power-probe.json</c>. Any other code (e.g. no permission) leaves the support <see cref="PowerSupport.Unknown"/>.
/// </summary>
public sealed class PowerService : IPowerService
{
    const int NvmlSuccess = 0;
    const int NvmlNotSupported = 3;
    const double ReadBackToleranceW = 0.5;

    readonly IGpu _gpu;
    readonly ILog _log;
    readonly string _cacheFile;
    readonly Lock _gate = new();
    PowerSupport _support = PowerSupport.Unknown;
    string? _supportDriver;

    /// <summary>Creates the service; nothing is read or written until <see cref="Probe"/> or <see cref="Apply"/>.</summary>
    readonly WriteGate? _writeGate;

    public PowerService(IGpu gpu, AppPaths paths, ILog log, WriteGate? gate = null)
    {
        _writeGate = gate;
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _gpu = gpu;
        _log = log;
        _cacheFile = Path.Combine(paths.DataDir, "power-probe.json");
    }

    /// <inheritdoc/>
    public PowerSupport Support { get { lock (_gate) return _support; } }

    /// <inheritdoc/>
    public PowerSupport Probe()
    {
        lock (_gate)
        {
            var info = _gpu.Info;
            if (info is null) return _support;
            if (_support != PowerSupport.Unknown && _supportDriver == info.Driver) return _support;

            var cache = JsonStore.Load<PowerProbeCache>(_cacheFile);
            if (cache != null && cache.Driver == info.Driver && cache.Support != PowerSupport.Unknown)
                return Remember(cache.Support, info.Driver, save: false);

            // Re-apply the CONFIGURED limit, never the enforced one: the enforced value can be temporarily lower
            // (thermal/platform) and writing it would lower the configured limit. If the configured limit cannot be
            // read, the driver does not expose power management here: report NotSupported without writing anything.
            double limit = _gpu.ConfiguredLimitW();
            if (double.IsNaN(limit) || limit <= 0)
            {
                _log.Info($"POWER probe driver {info.Driver}: configured limit unreadable -> NotSupported (no write)");
                return Remember(PowerSupport.NotSupported, info.Driver, save: true);
            }
            int rc = _gpu.SetPowerLimitW(limit);
            var result = rc switch
            {
                NvmlSuccess => PowerSupport.Adjustable,
                NvmlNotSupported => PowerSupport.NotSupported,
                _ => PowerSupport.Unknown,
            };
            _log.Info(string.Create(CultureInfo.InvariantCulture, $"POWER probe driver {info.Driver}: re-apply {limit:0.#} W rc={rc} -> {result}"));
            if (result == PowerSupport.Unknown) return _support = PowerSupport.Unknown;
            return Remember(result, info.Driver, save: true);
        }
    }

    /// <inheritdoc/>
    public ApplyResult Apply(double watts)
    {
        string requested = string.Create(CultureInfo.InvariantCulture, $"{watts:0.#} W");
        if (_writeGate?.TryBeginWrite() == false) return ApplyError.Fail(requested, ServiceTextKeys.TunerBusy);
        try
        {
            var info = _gpu.Info;
            if (info is null) return ApplyError.Fail(requested, ServiceTextKeys.GpuUnavailable, _gpu.LastError);
            if (double.IsNaN(watts) || watts <= 0
                || (!double.IsNaN(info.MinLimitW) && watts < info.MinLimitW)
                || (!double.IsNaN(info.MaxLimitW) && watts > info.MaxLimitW))
            {
                string range = string.Create(CultureInfo.InvariantCulture, $"{info.MinLimitW:0.#}..{info.MaxLimitW:0.#} W");
                return ApplyError.Fail(requested, ServiceTextKeys.PowerOutOfRange, range);
            }
            if (Probe() == PowerSupport.NotSupported)
                return ApplyError.Fail(requested, ServiceTextKeys.PowerNotSupported);

            lock (_gate)
            {
                int rc = _gpu.SetPowerLimitW(watts);
                // Le de volta o limite configurado (o que foi gravado); o aplicado pode estar mais baixo por um corte
                // temporario e daria falso erro de leitura.
                double back = _gpu.ConfiguredLimitW();
                if (double.IsNaN(back)) back = _gpu.EnforcedLimitW();
                string readBack = double.IsNaN(back) ? "?" : string.Create(CultureInfo.InvariantCulture, $"{back:0.#} W");
                if (rc == NvmlNotSupported) Remember(PowerSupport.NotSupported, info.Driver, save: true);
                if (rc != NvmlSuccess)
                {
                    string err = _gpu.ErrorText(rc);
                    _log.Warn($"POWER {requested} refused: {err}");
                    return ApplyError.Fail(requested, rc == NvmlNotSupported ? ServiceTextKeys.PowerNotSupported : ServiceTextKeys.PowerDriver, err, readBack);
                }
                if (_support == PowerSupport.Unknown) Remember(PowerSupport.Adjustable, info.Driver, save: true);
                if (double.IsNaN(back) || Math.Abs(back - watts) > ReadBackToleranceW)
                {
                    _log.Warn($"POWER {requested} read back {readBack}");
                    return ApplyError.Fail(requested, ServiceTextKeys.PowerReadBack, readBack, readBack);
                }
                _log.Info($"POWER {requested} ok");
                return new ApplyResult(true, requested, readBack);
            }
        }
        finally
        {
            _writeGate?.EndWrite();
        }
    }

    PowerSupport Remember(PowerSupport support, string driver, bool save)
    {
        _support = support;
        _supportDriver = driver;
        if (save)
        {
            try { JsonStore.Save(_cacheFile, new PowerProbeCache(driver, support, DateTime.UtcNow)); }
            catch (IOException e) { _log.Error("POWER probe cache", e); }
            catch (UnauthorizedAccessException e) { _log.Error("POWER probe cache", e); }
        }
        return support;
    }

    /// <summary>Probe result persisted per driver version.</summary>
    internal sealed record PowerProbeCache(string Driver, PowerSupport Support, DateTime CheckedUtc);
}
