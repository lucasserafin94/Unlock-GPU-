using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

public sealed class GpuMonitorTests : IDisposable
{
    readonly FakeGpu _gpu = new();
    readonly MemoryLog _log = new();
    readonly ManualTime _time = new();

    public void Dispose() => _gpu.Dispose();

    [Test]
    public void Offsets_BackToZero_RaisesDriverResetOnce()
    {
        using var m = new GpuMonitor(_gpu, _log, null, _time);
        int resets = 0;
        m.DriverReset += (_, _) => resets++;
        _gpu.ForceOffset(ClockDomain.Memory, 1000);
        m.PollOnce();
        Check.Equal(0, resets);
        _gpu.ForceOffset(ClockDomain.Memory, 0);
        m.PollOnce();
        Check.Equal(1, resets);
        _time.Advance(TimeSpan.FromMinutes(1));
        m.PollOnce();
        Check.Equal(1, resets);
    }

    [Test]
    public void Offsets_SetToZeroByApp_IsNotAReset()
    {
        var tracker = new OffsetTracker();
        var offsets = new OffsetService(_gpu, _log, tracker);
        using var m = new GpuMonitor(_gpu, _log, tracker, _time);
        int resets = 0;
        m.DriverReset += (_, _) => resets++;
        offsets.Apply(100, 1000);
        m.PollOnce();
        offsets.Apply(0, 0);
        m.PollOnce();
        Check.Equal(0, resets);
        offsets.Apply(100, 1000);
        m.PollOnce();
        _gpu.ForceOffset(ClockDomain.Core, 0);   // driver reset behind our back
        _gpu.ForceOffset(ClockDomain.Memory, 0);
        m.PollOnce();
        Check.Equal(1, resets);
    }

    [Test]
    public void ReadingsRecovering_AndReinit_RaiseDriverReset_Debounced()
    {
        using var m = new GpuMonitor(_gpu, _log, null, _time);
        int resets = 0;
        m.DriverReset += (_, _) => resets++;
        m.PollOnce();
        _gpu.Samples.Enqueue(new GpuSample { Time = DateTime.Now, Ok = false });
        m.PollOnce();
        m.PollOnce();                        // recovered
        Check.Equal(1, resets);
        _gpu.InitGeneration++;
        _time.Advance(TimeSpan.FromSeconds(5));
        m.PollOnce();                        // within debounce
        Check.Equal(1, resets);
        _gpu.InitGeneration++;
        _time.Advance(TimeSpan.FromSeconds(11));
        m.PollOnce();
        Check.Equal(2, resets);
    }

    [Test]
    public void History_And_Latest_AndHandlerExceptionsAreContained()
    {
        using var m = new GpuMonitor(_gpu, _log, null, _time);
        int seen = 0;
        m.SampleArrived += (_, _) => { seen++; throw new InvalidOperationException("ui bug"); };
        for (int i = 0; i < 5; i++) m.PollOnce();
        Check.Equal(5, seen);
        Check.Equal(5, m.History.Count);
        Check.True(m.Latest != null);
        Check.True(_log.Lines.Any(l => l.Contains("ui bug", StringComparison.Ordinal)));
    }

    [Test]
    public void History_DropsSamplesOlderThanWindow()
    {
        using var m = new GpuMonitor(_gpu, _log, null, _time);
        var t0 = new DateTime(2026, 11, 23, 10, 0, 0);
        _gpu.Samples.Enqueue(FakeGpu.Idle("P8", 405) with { Time = t0 });
        _gpu.Samples.Enqueue(FakeGpu.Idle("P8", 405) with { Time = t0.AddMinutes(10) });
        _gpu.Samples.Enqueue(FakeGpu.Idle("P8", 405) with { Time = t0.AddMinutes(31) });
        m.PollOnce(); m.PollOnce(); m.PollOnce();
        Check.Equal(2, m.History.Count);
    }

    [Test]
    public void StartStop_RunsInBackground()
    {
        using var m = new GpuMonitor(_gpu, _log);
        m.Interval = TimeSpan.FromMilliseconds(250);
        using var got = new ManualResetEventSlim();
        m.SampleArrived += (_, _) => got.Set();
        m.Start();
        Check.True(got.Wait(TimeSpan.FromSeconds(5)));
        m.Stop();
        int calls = _gpu.SampleCalls;
        Thread.Sleep(600);
        Check.Equal(calls, _gpu.SampleCalls);
        Check.Throws<ArgumentOutOfRangeException>(() => m.Interval = TimeSpan.Zero);
    }
}
