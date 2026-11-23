using System.Globalization;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Core and memory clock offsets through NVML (nvmlDeviceSetClockOffsets). Ranges come from the driver and are
/// checked before any call; every apply is read back.
/// </summary>
public sealed class OffsetService : IOffsetService
{
    readonly IGpu _gpu;
    readonly ILog _log;
    readonly OffsetTracker? _tracker;
    readonly Lock _gate = new();
    readonly WriteGate? _writeGate;

    /// <summary>Creates the service. <paramref name="tracker"/> (optional) receives the read-back values;
    /// <paramref name="gate"/> (optional) refuses writes while the auto-tuner runs.</summary>
    public OffsetService(IGpu gpu, ILog log, OffsetTracker? tracker = null, WriteGate? gate = null)
    {
        _writeGate = gate;
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(log);
        _gpu = gpu;
        _log = log;
        _tracker = tracker;
    }

    /// <inheritdoc/>
    public OffsetInfo? Get(ClockDomain domain) => _gpu.GetOffset(domain);

    /// <inheritdoc/>
    public ApplyResult Apply(int coreMHz, int memMHz)
    {
        string requested = Format(coreMHz, memMHz);
        if (_writeGate?.IsBlocked == true) return ApplyError.Fail(requested, ServiceTextKeys.TunerBusy);
        lock (_gate)
        {
            var core = _gpu.GetOffset(ClockDomain.Core);
            var mem = _gpu.GetOffset(ClockDomain.Memory);
            if (core is null || mem is null)
                return ApplyError.Fail(requested, ServiceTextKeys.OffsetUnavailable, _gpu.LastError);
            if (!InRange(coreMHz, core))
                return Reject(requested, "core", coreMHz, core);
            if (!InRange(memMHz, mem))
                return Reject(requested, "mem", memMHz, mem);

            // Tudo ou nada: se o nucleo for recusado, a memoria nem e tocada; se a memoria for recusada,
            // o nucleo volta ao valor anterior. Nunca fica metade aplicada.
            int rcCore = core.CurrentMHz == coreMHz ? 0 : _gpu.SetOffset(ClockDomain.Core, coreMHz);
            int rcMem = rcCore != 0 || mem.CurrentMHz == memMHz ? 0 : _gpu.SetOffset(ClockDomain.Memory, memMHz);
            if (rcCore == 0 && rcMem != 0 && core.CurrentMHz != coreMHz)
            {
                int rcBack = _gpu.SetOffset(ClockDomain.Core, core.CurrentMHz);
                _log.Warn($"OFFSET memory refused; core rolled back to {core.CurrentMHz} (rc={rcBack})");
            }

            int? core2 = _gpu.GetOffset(ClockDomain.Core)?.CurrentMHz;
            int? mem2 = _gpu.GetOffset(ClockDomain.Memory)?.CurrentMHz;
            _tracker?.Record(core2, mem2);
            string readBack = Format(core2, mem2);

            if (rcCore != 0 || rcMem != 0)
            {
                var parts = new List<string>();
                if (rcCore != 0) parts.Add("core: " + _gpu.ErrorText(rcCore));
                if (rcMem != 0) parts.Add("mem: " + _gpu.ErrorText(rcMem));
                string detail = string.Join("; ", parts);
                _log.Warn($"OFFSET {requested} refused: {detail}; read back {readBack}");
                return ApplyError.Fail(requested, ServiceTextKeys.OffsetDriver, detail, readBack);
            }
            if (core2 != coreMHz || mem2 != memMHz)
            {
                _log.Warn($"OFFSET {requested} read back {readBack}");
                return ApplyError.Fail(requested, ServiceTextKeys.OffsetReadBack, readBack, readBack);
            }
            _log.Info($"OFFSET {requested} ok");
            return new ApplyResult(true, requested, readBack);
        }
    }

    ApplyResult Reject(string requested, string domain, int value, OffsetInfo range)
    {
        string detail = string.Create(CultureInfo.InvariantCulture, $"{domain} {value} MHz not in {range.MinMHz}..{range.MaxMHz}");
        _log.Warn("OFFSET rejected: " + detail);
        return ApplyError.Fail(requested, ServiceTextKeys.OffsetOutOfRange, detail);
    }

    static bool InRange(int value, OffsetInfo range) => value >= range.MinMHz && value <= range.MaxMHz;

    static string Format(int? core, int? mem) =>
        $"core {Signed(core)} MHz; mem {Signed(mem)} MHz";

    static string Signed(int? v) => v is int x ? x.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "?";
}
