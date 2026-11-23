using UnlockGpu.Core.Model;
using UnlockGpu.Core.Oem;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>
/// Safety review of every write path against <see cref="FakeGpu"/>: nothing outside the driver ranges reaches the GPU,
/// invalid profile values are refused before any driver call, and the auto-tuner always puts the original offset back.
/// </summary>
public sealed class HardwareSafetyTests : TempDirTest
{
    readonly FakeGpu _gpu = new();
    readonly List<(ClockDomain Domain, int MHz)> _written = [];
    readonly TuneOptions _fast = new() { StepMHz = 500, SampleDuration = TimeSpan.FromMilliseconds(1), MinGainPercent = 1.0, RunVramTest = false };

    public HardwareSafetyTests()
    {
        _gpu.OffsetStored = (d, v) => { _written.Add((d, v)); return v; };
    }

    AutoTuner Tuner(OffsetService offsets, TunerFakeBenchmark bench) =>
        new(_gpu, offsets, bench, new TunerFakeVramTester(() => 0, _ => 0), Log) { TemperaturePollInterval = TimeSpan.FromMilliseconds(5) };

    int Mem() => _gpu.GetOffset(ClockDomain.Memory)!.CurrentMHz;
    int Core() => _gpu.GetOffset(ClockDomain.Core)!.CurrentMHz;

    [Test]
    public void Tuner_WithRealOffsetService_NeverWritesOutsideDriverRange()
    {
        _gpu.ForceOffset(ClockDomain.Core, 120);
        var offsets = new OffsetService(_gpu, Log);
        var bench = new TunerFakeBenchmark(Mem, o => TunerFakeBenchmark.Score(100 + o));
        var r = Tuner(offsets, bench).RunAsync(_fast with { MaxOffsetMHz = 99_999 }, null, CancellationToken.None).GetAwaiter().GetResult();

        Check.Equal(TuneStopReasons.MaxOffset, r.StopReasonKey);
        Check.Equal(6000, r.BestOffsetMHz);
        Check.True(_written.TrueForAll(w => w.Domain == ClockDomain.Memory ? w.MHz is >= -2000 and <= 6000 : w.MHz is >= -1000 and <= 1000));
        Check.True(_written.Exists(w => w == (ClockDomain.Memory, 6000)));
        Check.Equal(120, Core());
        Check.False(_written.Exists(w => w.Domain == ClockDomain.Core));
    }

    [Test]
    public void Tuner_CancelWithRealOffsetService_RestoresBothDomainsOnTheGpu()
    {
        _gpu.ForceOffset(ClockDomain.Core, -40);
        _gpu.ForceOffset(ClockDomain.Memory, 250);
        using var cts = new CancellationTokenSource();
        var bench = new TunerFakeBenchmark(Mem, o => TunerFakeBenchmark.Score(100 + o))
        {
            Before = async (_, call, _) => { if (call == 2) await cts.CancelAsync().ConfigureAwait(false); },
        };
        var r = Tuner(new OffsetService(_gpu, Log), bench).RunAsync(_fast, null, cts.Token).GetAwaiter().GetResult();

        Check.Equal(TuneStopReasons.Cancelled, r.StopReasonKey);
        Check.Equal(250, Mem());
        Check.Equal(-40, Core());
        Check.Equal((ClockDomain.Memory, 250), _written[^1]);
    }

    [Test]
    public void Tuner_DriverRefusesStep_RestoresOriginalOnTheGpu()
    {
        var bench = new TunerFakeBenchmark(Mem, o =>
        {
            if (o >= 500) _gpu.SetOffsetRc = 999; // driver starts refusing after the first raised step
            return TunerFakeBenchmark.Score(100 + o);
        });
        var r = Tuner(new OffsetService(_gpu, Log), bench).RunAsync(_fast, null, CancellationToken.None).GetAwaiter().GetResult();
        // The second step (1000) is refused; the restore to 0 is refused as well, so the GPU keeps 500 and says so.
        Check.Equal(TuneStopReasons.RestoreFailed, r.StopReasonKey);
        Check.Equal(500, Mem());

        _gpu.SetOffsetRc = 0;
        var bench2 = new TunerFakeBenchmark(Mem, o => TunerFakeBenchmark.Score(100 + o));
        var offsets = new FlakyOffsets(new OffsetService(_gpu, Log), failFirst: 2); // starts at 500: step 1000 ok, step 1500 and the first restore fail
        var r2 = new AutoTuner(_gpu, offsets, bench2, new TunerFakeVramTester(() => 0, _ => 0), Log)
            .RunAsync(_fast, null, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(TuneStopReasons.ApplyFailed, r2.StopReasonKey);
        Check.Equal(500, Mem());
    }

    [Test]
    public void Tuner_RestoreIsRetriedAfterAGpuReading()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o, mem: 0);
        var samplesAtRestore = new List<int>();
        rig.Offsets.Fails = (_, mem) =>
        {
            if (mem == 0) samplesAtRestore.Add(rig.Gpu.Samples);
            return mem == 200 || (mem == 0 && samplesAtRestore.Count == 1);
        };
        var r = rig.Run(new TuneOptions { StepMHz = 100, SampleDuration = TimeSpan.FromMilliseconds(1), MinGainPercent = 1.0 });

        Check.Equal(TuneStopReasons.ApplyFailed, r.StopReasonKey);
        Check.Equal(0, rig.Offsets.Mem);
        Check.Equal(2, samplesAtRestore.Count);
        Check.True(samplesAtRestore[1] > samplesAtRestore[0]); // a GPU reading (re-open) happened between the attempts
    }

    [Test]
    public void Tuner_RestoreNeverPossible_ReportsRestoreFailed()
    {
        var rig = new TunerRig(TuneTarget.Core, o => 100 + o);
        rig.Offsets.Fails = (core, _) => core == 200 || core == 0;
        var r = rig.Run(new TuneOptions { StepMHz = 100, SampleDuration = TimeSpan.FromMilliseconds(1), MinGainPercent = 1.0 });
        Check.Equal(TuneStopReasons.RestoreFailed, r.StopReasonKey);
        Check.Equal(100, rig.Offsets.Core);
        Check.True(rig.Log.Lines.Exists(l => l.Contains("could not restore", StringComparison.Ordinal)));
    }

    [Test]
    public void Tuner_RestoreThatThrows_DoesNotEscape()
    {
        var rig = new TunerRig(TuneTarget.Core, o => 100 + o);
        rig.Offsets.Fails = (_, _) => throw new InvalidOperationException("nvml gone");
        var r = rig.Run(new TuneOptions { StepMHz = 100, SampleDuration = TimeSpan.FromMilliseconds(1), MinGainPercent = 1.0 });
        Check.Equal(TuneStopReasons.RestoreFailed, r.StopReasonKey);
        Check.Equal(0, r.BestOffsetMHz);
    }

    [Test]
    public void ProfileWithInvalidValues_NeverReachesTheDriver()
    {
        var applier = NewApplier();
        var results = applier.Apply(new Profile { Name = "bad", CoreOffsetMHz = 5000, MemOffsetMHz = 99_999, VramLockMHz = 12_345, PowerLimitW = 999 });
        Check.Equal(3, results.Count);
        Check.True(results.All(r => !r.Ok));
        Check.Equal(0, _gpu.SetOffsetCalls);
        Check.Equal(0, _gpu.LockCalls);
        Check.Equal(0, _gpu.SetPowerCalls);

        applier.Apply(new Profile { Name = "nan", PowerLimitW = double.NaN });
        applier.Apply(new Profile { Name = "neg", PowerLimitW = -5 });
        applier.Apply(new Profile { Name = "low", PowerLimitW = 10 });
        applier.Apply(new Profile { Name = "slow", VramLockMHz = 810 }); // supported clock, but not a lockable (top two) level
        Check.Equal(0, _gpu.SetPowerCalls);
        Check.Equal(0, _gpu.LockCalls);
    }

    [Test]
    public void ProfileWithOneDomain_KeepsTheOtherDomainAndReadsBack()
    {
        _gpu.ForceOffset(ClockDomain.Core, 75);
        var results = NewApplier().Apply(new Profile { Name = "mem", MemOffsetMHz = 1500 });
        Check.True(results.Single().Ok);
        Check.Equal(75, Core());
        Check.Equal(1500, Mem());
        Check.Equal(1, _gpu.SetOffsetCalls);
        Check.Equal("core +75 MHz; mem +1500 MHz", results[0].ReadBackText);
    }

    [Test]
    public void ResetToDefaults_DoesNotTouchThePowerLimit()
    {
        _gpu.ForceOffset(ClockDomain.Memory, 2000);
        NewApplier().ResetToDefaults();
        Check.Equal(0, Mem());
        Check.Equal(1, _gpu.ResetCalls);
        Check.Equal(0, _gpu.SetPowerCalls);
    }

    [Test]
    public void EcChannelGuard_AcceptsOnlyReads()
    {
        Check.True(UniwillEc.IsRead(UniwillEc.EncodeRead(UniwillEc.Reg.CtgpOffset)));
        Check.False(UniwillEc.IsRead(UniwillEc.Encode(UniwillEc.Reg.CtgpOffset, 15, UniwillEc.OpWrite)));
        Check.False(UniwillEc.IsRead(UniwillEc.Encode(UniwillEc.Reg.CtgpOffset, 0, UniwillEc.OpWrite)));
        Check.False(UniwillEc.IsRead(UniwillEc.Encode(UniwillEc.Reg.CtgpOffset, 15, UniwillEc.OpRead)));
        Check.False(UniwillEc.IsRead(UniwillEc.EncodeRead(UniwillEc.Reg.CtgpOffset) | (1UL << 48)));
    }

    ProfileApplier NewApplier()
    {
        var offsets = new OffsetService(_gpu, Log);
        return new ProfileApplier(_gpu, offsets, new VramLockService(_gpu, Paths, Log), new PowerService(_gpu, Paths, Log), Log);
    }

    /// <summary>Wraps an offset service and refuses the first N applies that are not the very first one.</summary>
    sealed class FlakyOffsets(IOffsetService inner, int failFirst) : IOffsetService
    {
        int _calls;
        int _failed;

        public OffsetInfo? Get(ClockDomain domain) => inner.Get(domain);

        public ApplyResult Apply(int coreMHz, int memMHz)
        {
            if (_calls++ > 0 && _failed < failFirst)
            {
                _failed++;
                return new ApplyResult(false, $"{coreMHz}/{memMHz}", "?", "flaky");
            }
            return inner.Apply(coreMHz, memMHz);
        }
    }
}
