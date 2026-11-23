using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

public sealed class OffsetServiceTests : IDisposable
{
    readonly FakeGpu _gpu = new();
    readonly MemoryLog _log = new();

    public void Dispose() => _gpu.Dispose();

    [Test]
    public void OutOfRange_IsRejectedBeforeCallingDriver()
    {
        var svc = new OffsetService(_gpu, _log);
        var core = svc.Apply(1500, 0);
        Check.False(core.Ok);
        Check.Equal(ServiceTextKeys.OffsetOutOfRange, ApplyError.Split(core.ErrorText).Key);
        var mem = svc.Apply(0, 6001);
        Check.False(mem.Ok);
        Check.Contains("mem 6001", ApplyError.Split(mem.ErrorText).Detail);
        Check.False(svc.Apply(-1001, 0).Ok);
        Check.Equal(0, _gpu.SetOffsetCalls);
    }

    [Test]
    public void Apply_ReadsBackAndRecordsTracker()
    {
        var tracker = new OffsetTracker();
        var r = new OffsetService(_gpu, _log, tracker).Apply(150, 3000);
        Check.True(r.Ok);
        Check.Equal("core +150 MHz; mem +3000 MHz", r.RequestedText);
        Check.Equal(r.RequestedText, r.ReadBackText);
        Check.Equal(150, tracker.CoreMHz);
        Check.Equal(3000, tracker.MemMHz);
        Check.Equal(2, _gpu.SetOffsetCalls);
    }

    [Test]
    public void Apply_SkipsDomainsAlreadyAtValue()
    {
        _gpu.ForceOffset(ClockDomain.Core, 100);
        Check.True(new OffsetService(_gpu, _log).Apply(100, 500).Ok);
        Check.Equal(1, _gpu.SetOffsetCalls);
    }

    [Test]
    public void ReadBackMismatch_IsAFailure()
    {
        _gpu.OffsetStored = (d, v) => d == ClockDomain.Memory ? Math.Min(v, 3000) : v;
        var r = new OffsetService(_gpu, _log).Apply(0, 4000);
        Check.False(r.Ok);
        Check.Equal(ServiceTextKeys.OffsetReadBack, ApplyError.Split(r.ErrorText).Key);
        Check.Contains("mem +3000", r.ReadBackText);
    }

    [Test]
    public void DriverError_IsReported()
    {
        _gpu.SetOffsetRc = 4;
        var r = new OffsetService(_gpu, _log).Apply(100, 0);
        Check.False(r.Ok);
        var (key, detail) = ApplyError.Split(r.ErrorText);
        Check.Equal(ServiceTextKeys.OffsetDriver, key);
        Check.Contains("Insufficient Permissions", detail);
    }

    [Test]
    public void Unreadable_IsUnavailable()
    {
        _gpu.OffsetsReadable = false;
        var r = new OffsetService(_gpu, _log).Apply(0, 0);
        Check.Equal(ServiceTextKeys.OffsetUnavailable, ApplyError.Split(r.ErrorText).Key);
    }
}

public sealed class PowerServiceTests : TempDirTest
{
    readonly FakeGpu _gpu = new();

    [Test]
    public void Probe_NotSupported_IsCachedPerDriver()
    {
        _gpu.SetPowerRc = 3;
        Check.Equal(PowerSupport.NotSupported, new PowerService(_gpu, Paths, Log).Probe());
        Check.Equal(1, _gpu.SetPowerCalls);

        var again = new PowerService(_gpu, Paths, Log);
        Check.Equal(PowerSupport.Unknown, again.Support);
        Check.Equal(PowerSupport.NotSupported, again.Probe());
        Check.Equal(PowerSupport.NotSupported, again.Probe());
        Check.Equal(1, _gpu.SetPowerCalls);
    }

    [Test]
    public void Probe_ReappliesConfiguredLimit()
    {
        _gpu.EnforcedW = 160;
        Check.Equal(PowerSupport.Adjustable, new PowerService(_gpu, Paths, Log).Probe());
        Check.Near(160, _gpu.EnforcedW);
    }

    [Test]
    public void Probe_ConfiguredUnreadable_IsNotSupported_WithoutWriting()
    {
        _gpu.ConfiguredUnreadable = true;
        Check.Equal(PowerSupport.NotSupported, new PowerService(_gpu, Paths, Log).Probe());
        Check.Equal(0, _gpu.SetPowerCalls);
    }

    [Test]
    public void Probe_OtherErrors_AreNotCached()
    {
        _gpu.SetPowerRc = 4;
        Check.Equal(PowerSupport.Unknown, new PowerService(_gpu, Paths, Log).Probe());
        _gpu.SetPowerRc = 0;
        Check.Equal(PowerSupport.Adjustable, new PowerService(_gpu, Paths, Log).Probe());
        Check.Equal(2, _gpu.SetPowerCalls);
    }

    [Test]
    public void Probe_NewDriver_ProbesAgain()
    {
        _gpu.SetPowerRc = 3;
        new PowerService(_gpu, Paths, Log).Probe();
        _gpu.Info = _gpu.Info! with { Driver = "622.00" };
        _gpu.SetPowerRc = 0;
        Check.Equal(PowerSupport.Adjustable, new PowerService(_gpu, Paths, Log).Probe());
        Check.Equal(2, _gpu.SetPowerCalls);
    }

    [Test]
    public void Apply_NotSupported_DoesNotCallDriverAgain()
    {
        _gpu.SetPowerRc = 3;
        var svc = new PowerService(_gpu, Paths, Log);
        svc.Probe();
        var r = svc.Apply(150);
        Check.False(r.Ok);
        Check.Equal(ServiceTextKeys.PowerNotSupported, ApplyError.Split(r.ErrorText).Key);
        Check.Equal(1, _gpu.SetPowerCalls);
    }

    [Test]
    public void Apply_OutOfRange_IsRejected()
    {
        var svc = new PowerService(_gpu, Paths, Log);
        Check.Equal(ServiceTextKeys.PowerOutOfRange, ApplyError.Split(svc.Apply(200).ErrorText).Key);
        Check.Equal(ServiceTextKeys.PowerOutOfRange, ApplyError.Split(svc.Apply(double.NaN).ErrorText).Key);
        Check.Equal(0, _gpu.SetPowerCalls);
    }

    [Test]
    public void Apply_Ok_ReadsBack()
    {
        var r = new PowerService(_gpu, Paths, Log).Apply(150);
        Check.True(r.Ok);
        Check.Equal("150 W", r.ReadBackText);
    }
}
