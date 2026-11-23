using UnlockGpu.Core.Model;

namespace UnlockGpu.Core.Services;

/// <summary>Running sums for a session summary (no sample list is kept). Not thread-safe.</summary>
public sealed class SessionStats
{
    int _samples;
    int _load;
    double _sumPower;
    double _sumCore;
    double _peakPower = double.NaN;
    uint _maxTemp;
    readonly Dictionary<uint, int> _memClocks = [];
    readonly Dictionary<string, int> _reasons = new(StringComparer.Ordinal);

    /// <summary>True when the sample counts as "under load" for the statistics.</summary>
    public static bool IsLoad(GpuSample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Ok && s.UtilGpu >= SessionRecorder.LoadUtilPercent && !double.IsNaN(s.PowerW) && s.CoreMHz > 0 && s.MemMHz > 0;
    }

    /// <summary>Adds one sample.</summary>
    public void Add(GpuSample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _samples++;
        if (s.TempC > _maxTemp) _maxTemp = s.TempC;
        if (!IsLoad(s)) return;
        _load++;
        _sumPower += s.PowerW;
        _sumCore += s.CoreMHz;
        if (double.IsNaN(_peakPower) || s.PowerW > _peakPower) _peakPower = s.PowerW;
        _memClocks[s.MemMHz] = _memClocks.GetValueOrDefault(s.MemMHz) + 1;
        foreach (var tag in ClockReasonText.Tags(s.Reasons))
            _reasons[tag.Key] = _reasons.GetValueOrDefault(tag.Key) + 1;
    }

    /// <summary>Builds the summary. Averages and peak are NaN when there were no load samples; shares are 0..1 of load samples;
    /// MaxTempC is over all samples.</summary>
    public SessionSummary ToSummary(string csvPath, TimeSpan duration)
    {
        double n = _load;
        return new SessionSummary(csvPath, duration, _samples, _load,
            _load > 0 ? _sumPower / n : double.NaN,
            _load > 0 ? _peakPower : double.NaN,
            _load > 0 ? _sumCore / n : double.NaN,
            _maxTemp,
            _memClocks.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value / n),
            _reasons.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value / n, StringComparer.Ordinal));
    }
}
