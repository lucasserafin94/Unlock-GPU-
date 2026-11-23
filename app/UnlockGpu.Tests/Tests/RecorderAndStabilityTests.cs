using System.Globalization;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

public sealed class SessionRecorderTests : TempDirTest
{
    readonly ManualTime _time = new();

    [Test]
    public void Summary_Math_IncludingNaN()
    {
        using var rec = new SessionRecorder(Paths, Log, _time);
        rec.Start("test");
        rec.Add(FakeGpu.Load(100, 2000, 14001, ClockReasons.SwPowerCap, temp: 80));
        rec.Add(FakeGpu.Load(120, 2200, 11001, ClockReasons.None, temp: 82));
        rec.Add(FakeGpu.Load(double.NaN, 2100, 14001, temp: 85));                // no power reading: not a load sample
        rec.Add(FakeGpu.Idle("P8", 405));                                         // idle
        rec.Add(new GpuSample { Time = DateTime.Now, Ok = false });               // failed reading: ignored
        _time.Advance(TimeSpan.FromSeconds(90));

        var s = Check.NotNull(rec.Stop());
        Check.Equal(4, s.Samples);
        Check.Equal(2, s.LoadSamples);
        Check.Near(110, s.AvgPowerW);
        Check.Near(120, s.PeakPowerW);
        Check.Near(2100, s.AvgCoreMHz);
        Check.Equal(85u, s.MaxTempC);
        Check.Equal(TimeSpan.FromSeconds(90), s.Duration);
        Check.Near(0.5, s.MemClockShare[14001]);
        Check.Near(0.5, s.MemClockShare[11001]);
        Check.Near(0.5, s.ReasonShare["reason.swPowerCap"]);
        Check.Near(0.5, s.ReasonShare["reason.none"]);
        Check.False(rec.IsRecording);
    }

    [Test]
    public void Summary_WithoutLoad_IsNaN()
    {
        using var rec = new SessionRecorder(Paths, Log, _time);
        rec.Start();
        rec.Add(FakeGpu.Idle("P8", 405));
        var s = Check.NotNull(rec.Live());
        Check.Equal(1, s.Samples);
        Check.Equal(0, s.LoadSamples);
        Check.NaN(s.AvgPowerW);
        Check.NaN(s.PeakPowerW);
        Check.NaN(s.AvgCoreMHz);
        Check.Equal(0, s.MemClockShare.Count);
    }

    [Test]
    public void Csv_IsInvariant_AndReadableWhileRecording()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        try
        {
            using var rec = new SessionRecorder(Paths, Log, _time);
            rec.Start("jogo leve; x");
            rec.Add(FakeGpu.Load(123.45, 2000, 14001, ClockReasons.SwPowerCap | ClockReasons.HwSlowdown));
            rec.Add(FakeGpu.Load(double.NaN, 2000, 14001));
            string path = Check.NotNull(rec.CurrentPath);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Check.True(lines[0].StartsWith("# UnlockGPU session v1;", StringComparison.Ordinal));
            Check.True(lines[1].StartsWith("time;pstate;core_mhz", StringComparison.Ordinal));
            var f = lines[2].Split(';');
            Check.Equal(12, f.Length);
            Check.Equal("123.5", f[6]);
            Check.Equal("175.0", f[7]);
            Check.Equal("SwPowerCap|HwSlowdown", f[11]);
            Check.Equal("", lines[3].Split(';')[6]);
            Check.True(Path.GetFileName(path).StartsWith("session-20261123-", StringComparison.Ordinal));
            Check.Contains("-jogo_leve__x", path);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Test]
    public void ListSessions_NewestFirst_AndNoDuplicateNames()
    {
        using var rec = new SessionRecorder(Paths, Log, _time);
        rec.Start(); string a = rec.CurrentPath!; rec.Stop();
        rec.Start(); string b = rec.CurrentPath!; rec.Stop();   // same second: gets a suffix
        _time.Advance(TimeSpan.FromMinutes(1));
        rec.Start(); string c = rec.CurrentPath!; rec.Stop();
        Check.False(a == b);
        var list = rec.ListSessions();
        Check.Equal(3, list.Count);
        Check.Equal(c, list[0]);
    }

    [Test]
    public void Add_WhenNotRecording_IsIgnored()
    {
        using var rec = new SessionRecorder(Paths, Log, _time);
        rec.Add(FakeGpu.Idle("P8", 405));
        Check.Equal(null, rec.Live());
        Check.Equal(null, rec.Stop());
    }
}

public sealed class StabilityMonitorTests
{
    readonly MemoryLog _log = new();
    readonly ManualTime _time = new();
    readonly FakeEventSource _events = new();

    StabilityMonitor Create() => new(_log, _events, _time);

    [Test]
    public void Throttling_WarnsAfterTenConsecutiveLoadSamples()
    {
        var m = Create();
        int changes = 0;
        m.Changed += (_, _) => changes++;
        for (int i = 0; i < 9; i++) m.Feed(FakeGpu.Load(170, 2000, 14001, ClockReasons.SwPowerCap));
        Check.Equal(Health.Good, m.Current.Level);
        m.Feed(FakeGpu.Load(170, 2000, 14001, ClockReasons.SwPowerCap));
        Check.Equal(Health.Warning, m.Current.Level);
        Check.Equal(ServiceTextKeys.HealthThrottling, m.Current.ReasonKeys[0]);
        Check.Equal(1, changes);
        m.Feed(FakeGpu.Load(150, 2000, 14001));
        Check.Equal(Health.Good, m.Current.Level);
        Check.Equal(2, changes);
    }

    [Test]
    public void Throttling_StreakBrokenByIdleOrInfoReasons()
    {
        var m = Create();
        for (int i = 0; i < 9; i++) m.Feed(FakeGpu.Load(170, 2000, 14001, ClockReasons.HwThermalSlowdown));
        m.Feed(FakeGpu.Idle("P8", 405));
        for (int i = 0; i < 9; i++) m.Feed(FakeGpu.Load(170, 2000, 14001, ClockReasons.HwThermalSlowdown));
        m.Feed(FakeGpu.Load(170, 2000, 14001, ClockReasons.GpuIdle));
        Check.Equal(Health.Good, m.Current.Level);
    }

    [Test]
    public void Temperature_Thresholds()
    {
        var m = Create();
        m.Feed(FakeGpu.Load(150, 2000, 14001, temp: 86));
        Check.Equal(Health.Good, m.Current.Level);
        m.Feed(FakeGpu.Load(150, 2000, 14001, temp: 87));
        Check.Equal(Health.Warning, m.Current.Level);
        Check.Equal(ServiceTextKeys.HealthHot, m.Current.ReasonKeys[0]);
        m.Feed(FakeGpu.Load(150, 2000, 14001, temp: 92));
        Check.Equal(Health.Critical, m.Current.Level);
        Check.Equal(ServiceTextKeys.HealthOverheat, m.Current.ReasonKeys[0]);
        m.Feed(FakeGpu.Load(150, 2000, 14001, temp: 70));
        Check.Equal(Health.Good, m.Current.Level);
    }

    [Test]
    public void BenchmarkDrop_IsCriticalUntilCleared()
    {
        var m = Create();
        m.ReportBenchmarkDrop(4.9);
        Check.Equal(Health.Good, m.Current.Level);
        m.ReportBenchmarkDrop(5);
        Check.Equal(Health.Critical, m.Current.Level);
        Check.Equal(ServiceTextKeys.HealthBenchmarkDrop, m.Current.ReasonKeys[0]);
        m.ReportBenchmarkDrop(1);
        Check.Equal(Health.Good, m.Current.Level);
    }

    [Test]
    public void SystemEvents_SinceStart_AreCritical_AndPolledAtMostEveryMinute()
    {
        _events.Events.Add(new SystemEventRecord(_time.Now.UtcDateTime.AddHours(-1), "Microsoft-Windows-Kernel-Power", 41)); // before start
        var m = Create();
        m.Feed(FakeGpu.Idle("P8", 405));
        Check.Equal(Health.Good, m.Current.Level);
        Check.Equal(1, _events.Queries);

        _events.Events.Add(new SystemEventRecord(_time.Now.UtcDateTime.AddSeconds(10), "Microsoft-Windows-WHEA-Logger", 17));
        _time.Advance(TimeSpan.FromSeconds(30));
        m.Feed(FakeGpu.Idle("P8", 405));
        Check.Equal(1, _events.Queries);
        _time.Advance(TimeSpan.FromSeconds(31));
        m.Feed(FakeGpu.Idle("P8", 405));
        Check.Equal(2, _events.Queries);
        Check.Equal(Health.Critical, m.Current.Level);
        Check.Equal(ServiceTextKeys.HealthSystemEvents, m.Current.ReasonKeys[0]);

        var list = m.RecentSystemEvents();
        Check.Equal(1, list.Count);
        Check.Contains("WHEA-Logger  17", list[0]);
    }

    [Test]
    public void EventQuery_Xml_IsParsed()
    {
        const string xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>" +
            "<Provider Name='Microsoft-Windows-Kernel-Power' /><EventID>41</EventID>" +
            "<TimeCreated SystemTime='2026-11-20T10:11:12.1234567Z' /></System></Event>";
        var e = Check.NotNull(WindowsSystemEventSource.Parse(xml));
        Check.Equal("Microsoft-Windows-Kernel-Power", e.Provider);
        Check.Equal(41, e.EventId);
        Check.Equal(new DateTime(2026, 11, 20, 10, 11, 12, DateTimeKind.Utc), e.TimeUtc.AddTicks(-(e.TimeUtc.Ticks % TimeSpan.TicksPerSecond)));
        Check.Contains("EventID=41", WindowsSystemEventSource.BuildQuery(DateTime.UtcNow));
    }
}
