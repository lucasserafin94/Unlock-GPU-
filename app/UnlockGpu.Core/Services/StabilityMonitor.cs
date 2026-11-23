using System.ComponentModel;
using System.Globalization;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Health from the sample stream plus external signals.
/// Warning: performance-costing clock reasons in <see cref="ThrottleSamples"/> consecutive load samples, or temperature
/// &gt;= <see cref="WarnTempC"/>. Critical: temperature &gt;= <see cref="CriticalTempC"/>, a benchmark drop &gt;=
/// <see cref="BenchmarkDropPercent"/>, or Kernel-Power 41 / WHEA-Logger events since the monitor was created
/// (System log read-only, polled at most every <see cref="EventPollInterval"/>).
/// </summary>
public sealed class StabilityMonitor : IStabilityMonitor
{
    /// <summary>Consecutive throttled load samples for a warning.</summary>
    public const int ThrottleSamples = 10;
    /// <summary>GPU utilisation (%) from which a sample counts as "under load".</summary>
    public const uint LoadUtilPercent = 90;
    /// <summary>Warning temperature (C).</summary>
    public const uint WarnTempC = 87;
    /// <summary>Critical temperature (C).</summary>
    public const uint CriticalTempC = 92;
    /// <summary>Benchmark drop (%) considered critical.</summary>
    public const double BenchmarkDropPercent = 5;
    /// <summary>Minimum time between two event log queries from <see cref="Feed"/>.</summary>
    public static readonly TimeSpan EventPollInterval = TimeSpan.FromSeconds(60);

    readonly ILog _log;
    readonly ISystemEventSource _events;
    readonly TimeProvider _time;
    readonly DateTime _sinceUtc;
    readonly Lock _gate = new();

    int _throttleStreak;
    uint _tempC;
    bool _benchDrop;
    IReadOnlyList<SystemEventRecord> _systemEvents = [];
    DateTimeOffset _lastEventPoll = DateTimeOffset.MinValue;
    bool _eventErrorLogged;
    volatile HealthReport _current = new(Health.Good, []);

    /// <summary>Creates the monitor; "since app start" is the moment of construction.</summary>
    /// <param name="log">Log.</param>
    /// <param name="events">System log source (defaults to <see cref="WindowsSystemEventSource"/>).</param>
    /// <param name="time">Clock (tests).</param>
    public StabilityMonitor(ILog log, ISystemEventSource? events = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _events = events ?? new WindowsSystemEventSource();
        _time = time ?? TimeProvider.System;
        _sinceUtc = _time.GetUtcNow().UtcDateTime;
    }

    /// <inheritdoc/>
    /// <remarks>Leitura sem lock (campo volatile); escrito so sob <c>_gate</c> em <c>Recompute</c>.</remarks>
    public HealthReport Current => _current;

    /// <inheritdoc/>
    public event EventHandler<HealthReport>? Changed;

    /// <inheritdoc/>
    public void Feed(GpuSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        HealthReport? changed = null;
        bool poll;
        lock (_gate)
        {
            if (sample.Ok)
            {
                bool load = sample.UtilGpu >= LoadUtilPercent;
                _throttleStreak = load && ClockReasonText.CostsPerformance(sample.Reasons) ? _throttleStreak + 1 : 0;
                _tempC = sample.TempC;
            }
            // decide sob o lock e marca o instante, para que so uma thread faca a consulta
            var now = _time.GetUtcNow();
            poll = now - _lastEventPoll >= EventPollInterval;
            if (poll) _lastEventPoll = now;
            else changed = Recompute();
        }
        if (poll)
        {
            // consulta ao event log (wevtapi) fora do lock: nao bloqueia Current / ReportBenchmarkDrop
            var events = QueryEvents();
            lock (_gate)
            {
                StoreEvents(events);
                changed = Recompute();
            }
        }
        if (changed != null) Changed?.Invoke(this, changed);
    }

    /// <inheritdoc/>
    public void ReportBenchmarkDrop(double percent)
    {
        HealthReport? changed;
        lock (_gate)
        {
            _benchDrop = !double.IsNaN(percent) && percent >= BenchmarkDropPercent;
            changed = Recompute();
        }
        if (changed != null) Changed?.Invoke(this, changed);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> RecentSystemEvents()
    {
        lock (_gate) _lastEventPoll = _time.GetUtcNow();
        var queried = QueryEvents();
        HealthReport? changed;
        IReadOnlyList<SystemEventRecord> events;
        lock (_gate)
        {
            StoreEvents(queried);
            events = _systemEvents;
            changed = Recompute();
        }
        if (changed != null) Changed?.Invoke(this, changed);
        return events.Select(e => string.Create(CultureInfo.InvariantCulture,
            $"{e.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {e.Provider}  {e.EventId}")).ToArray();
    }

    /// <summary>Consulta o System log sem segurar <c>_gate</c>; null em caso de erro (logado uma vez).</summary>
    IReadOnlyList<SystemEventRecord>? QueryEvents()
    {
        try
        {
            return _events.Query(_sinceUtc);
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException)
        {
            bool first;
            lock (_gate)
            {
                first = !_eventErrorLogged;
                _eventErrorLogged = true;
            }
            if (first) _log.Error("STABILITY event log query", e);
            return null;
        }
    }

    /// <summary>Guarda o resultado da consulta; chamar sob <c>_gate</c>.</summary>
    void StoreEvents(IReadOnlyList<SystemEventRecord>? events)
    {
        if (events == null) return;
        _systemEvents = events;
        _eventErrorLogged = false;
    }

    HealthReport? Recompute()
    {
        var critical = new List<string>();
        var warning = new List<string>();
        if (_tempC >= CriticalTempC) critical.Add(ServiceTextKeys.HealthOverheat);
        else if (_tempC >= WarnTempC) warning.Add(ServiceTextKeys.HealthHot);
        if (_benchDrop) critical.Add(ServiceTextKeys.HealthBenchmarkDrop);
        if (_systemEvents.Count > 0) critical.Add(ServiceTextKeys.HealthSystemEvents);
        if (_throttleStreak >= ThrottleSamples) warning.Add(ServiceTextKeys.HealthThrottling);

        var level = critical.Count > 0 ? Health.Critical : warning.Count > 0 ? Health.Warning : Health.Good;
        var report = new HealthReport(level, [.. critical, .. warning]);
        if (report.Level == _current.Level && report.ReasonKeys.SequenceEqual(_current.ReasonKeys, StringComparer.Ordinal)) return null;
        if (report.Level != _current.Level) _log.Info($"STABILITY {_current.Level} -> {report.Level} ({string.Join(", ", report.ReasonKeys)})");
        _current = report;
        return report;
    }
}
