using UnlockGpu.Core.Bench;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>Tests for <see cref="AutoTuner"/>, <see cref="VramPatterns"/> and <see cref="VramPlan"/>. No hardware: the GPU,
/// offsets, benchmark and VRAM tester are fakes from BenchFakes.cs (Tuner* names, distinct from Fakes/FakeGpu). Each test throws on failure.</summary>
public sealed class BenchTunerTests
{
    readonly uint[] _baseAddresses = [0, 1, 12345, 0xFFFFFFF0u];
    readonly long _gb = 1L << 30;

    readonly TuneOptions _fast = new() { StepMHz = 100, SampleDuration = TimeSpan.FromMilliseconds(1), MinGainPercent = 1.0 };

    [Test]
    public void TunerStopsAfterTwoFlatStepsAndAppliesBest()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + Math.Min(o, 300) / 10.0, core: 50);
        var progress = new TunerListProgress();
        var r = rig.Run(_fast, progress);
        BenchAssert.Equal(TuneStopReasons.NoGain, r.StopReasonKey, "stop");
        BenchAssert.Equal(300, r.BestOffsetMHz, "best");
        BenchAssert.Equal(300, rig.Offsets.Mem, "applied memory offset");
        BenchAssert.Equal(50, rig.Offsets.Core, "core offset untouched");
        BenchAssert.That(rig.Offsets.Applied.TrueForAll(a => a.Core == 50), "core re-applied with its own value");
        BenchAssert.Equal(100.0, r.BaselineScore, "baseline");
        BenchAssert.Equal(130.0, r.BestScore, "best score");
        BenchAssert.Equal(6, r.Steps.Count, "steps (baseline + 100..500)");
        BenchAssert.That(r.Steps[0].GainPercent == null, "baseline has no gain");
        BenchAssert.Equal(r.Steps.Count, progress.Items.Count, "every step reported");
        BenchAssert.Equal(rig.Vram.Calls, r.Steps.Count, "memory target runs the VRAM test each step");
        BenchAssert.That(rig.Vram.Fractions.TrueForAll(f => f == AutoTuner.VramTestFraction), "VRAM test uses half the free memory");
    }

    [Test]
    public void TunerVramErrorsKeepsLastErrorFreeOffset()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o, vramErrors: o => o >= 300 ? 7 : 0);
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.VramErrors, r.StopReasonKey, "stop");
        BenchAssert.Equal(200, r.BestOffsetMHz, "best");
        BenchAssert.Equal(200, rig.Offsets.Mem, "applied");
        BenchAssert.Equal(7L, r.Steps[^1].VramErrors, "errors reported on the failing step");
    }

    [Test]
    public void TunerBaselineVramErrorsStopsWithoutChanging()
    {
        var rig = new TunerRig(TuneTarget.Memory, _ => 100, vramErrors: _ => 1, mem: 400);
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.VramErrors, r.StopReasonKey, "stop");
        BenchAssert.Equal(0, rig.Offsets.Applied.Count, "nothing applied");
        BenchAssert.Equal(400, r.BestOffsetMHz, "best is the current offset");
    }

    [Test]
    public void TunerScoreDropFallsBack()
    {
        var rig = new TunerRig(TuneTarget.Core, o => o <= 200 ? 100 + o / 10.0 : 90);
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.ScoreDrop, r.StopReasonKey, "stop");
        BenchAssert.Equal(200, r.BestOffsetMHz, "best");
        BenchAssert.Equal(200, rig.Offsets.Core, "applied core");
        BenchAssert.Equal(0, rig.Vram.Calls, "core target skips the VRAM test");
    }

    [Test]
    public void TunerRespectsMaxOffsetAndDriverMax()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o);
        var r = rig.Run(_fast with { MaxOffsetMHz = 250 });
        BenchAssert.Equal(TuneStopReasons.MaxOffset, r.StopReasonKey, "stop (option)");
        BenchAssert.Equal(250, r.BestOffsetMHz, "last step clamped to the option");
        BenchAssert.That(rig.Offsets.Applied.TrueForAll(a => a.Mem <= 250), "never above the option");

        var rig2 = new TunerRig(TuneTarget.Memory, o => 100 + o, max: 300);
        var r2 = rig2.Run(_fast with { MaxOffsetMHz = 5000 });
        BenchAssert.Equal(300, r2.BestOffsetMHz, "clamped to driver max");
        BenchAssert.That(rig2.Offsets.Applied.TrueForAll(a => a.Mem <= 300), "never above the driver max");
    }

    [Test]
    public void TunerCancelRestoresOriginal()
    {
        using var cts = new CancellationTokenSource();
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o, core: -50, mem: 100);
        rig.Bench.Before = async (offset, call, ct) =>
        {
            if (call == 2) await cts.CancelAsync().ConfigureAwait(false);
        };
        var r = rig.Run(_fast, ct: cts.Token);
        BenchAssert.Equal(TuneStopReasons.Cancelled, r.StopReasonKey, "stop");
        BenchAssert.Equal(100, rig.Offsets.Mem, "memory restored");
        BenchAssert.Equal(-50, rig.Offsets.Core, "core restored");
        BenchAssert.Equal((-50, 100), rig.Offsets.Applied[^1], "last apply is the restore");
        BenchAssert.Equal(100, r.BestOffsetMHz, "result reports the original offset");
    }

    [Test]
    public void TunerDeviceRemovedRestoresOriginal()
    {
        var rig = new TunerRig(TuneTarget.Core,
            o => o >= 200 ? new BenchResult(0, TimeSpan.Zero, false, BenchErrorKeys.DeviceRemoved) : TunerFakeBenchmark.Score(100 + o));
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.DeviceRemoved, r.StopReasonKey, "stop");
        BenchAssert.Equal(0, rig.Offsets.Core, "restored");
        BenchAssert.Equal(0, r.BestOffsetMHz, "result is the original");
    }

    [Test]
    public void TunerBenchmarkThrowsRestoresOriginal()
    {
        var rig = new TunerRig(TuneTarget.Core, o => o >= 200 ? throw new InvalidOperationException("boom") : TunerFakeBenchmark.Score(100 + o));
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.Error, r.StopReasonKey, "stop");
        BenchAssert.Equal(0, rig.Offsets.Core, "restored");
        BenchAssert.That(rig.Log.Lines.Exists(l => l.StartsWith('E')), "error logged");
    }

    [Test]
    public void TunerApplyFailureRestoresOriginal()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o, mem: 0);
        rig.Offsets.Fails = (_, mem) => mem == 200;
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.ApplyFailed, r.StopReasonKey, "stop");
        BenchAssert.Equal(0, rig.Offsets.Mem, "restored");
    }

    [Test]
    public void TunerHotDuringMeasurementStopsAndKeepsBest()
    {
        var rig = new TunerRig(TuneTarget.Core, o => 100 + o);
        rig.Bench.Before = async (offset, call, ct) =>
        {
            if (offset < 200) return;
            rig.Gpu.TempC = 90;
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // only the temperature watchdog can end this
        };
        var r = rig.Run(_fast with { MaxTempC = 87 });
        BenchAssert.Equal(TuneStopReasons.Temperature, r.StopReasonKey, "stop");
        BenchAssert.Equal(100, r.BestOffsetMHz, "best before overheating");
        BenchAssert.Equal(100, rig.Offsets.Core, "best applied");
    }

    [Test]
    public void TunerHotAtStartDoesNothing()
    {
        var rig = new TunerRig(TuneTarget.Core, o => 100 + o);
        rig.Gpu.TempC = 95;
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.Temperature, r.StopReasonKey, "stop");
        BenchAssert.Equal(0, rig.Bench.Calls, "no benchmark");
        BenchAssert.Equal(0, rig.Offsets.Applied.Count, "nothing applied");
    }

    [Test]
    public void TunerOffsetsUnreadableReportsUnavailable()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o);
        rig.Offsets.Readable = false;
        var r = rig.Run(_fast);
        BenchAssert.Equal(TuneStopReasons.Unavailable, r.StopReasonKey, "stop");
        BenchAssert.Equal(0, rig.Offsets.Applied.Count, "nothing applied");
    }

    [Test]
    public void TunerRejectsBadOptions()
    {
        var rig = new TunerRig(TuneTarget.Memory, o => 100 + o);
        bool threw = false;
        try { rig.Run(_fast with { StepMHz = 0 }); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        BenchAssert.That(threw, "StepMHz 0 rejected");
    }

    [Test]
    public void VramPatternsCountsFlippedWords()
    {
        foreach (uint baseAddress in _baseAddresses)
            for (uint pattern = 0; pattern < VramPatterns.Count; pattern++)
            {
                var data = new uint[1003]; // not a multiple of any vector width
                for (int i = 0; i < data.Length; i++) data[i] = VramPatterns.Expected(unchecked(baseAddress + (uint)i), pattern);
                BenchAssert.Equal(0L, VramPatterns.CountMismatches(data, baseAddress, pattern), $"clean p{pattern} b{baseAddress}");
                data[0] ^= 1; data[17] ^= 0x80000000u; data[^1] = ~data[^1];
                BenchAssert.Equal(3L, VramPatterns.CountMismatches(data, baseAddress, pattern), $"3 flips p{pattern} b{baseAddress}");
            }
        BenchAssert.Equal(0x55555555u, VramPatterns.Expected(0, VramPatterns.Alternating), "alternating even");
        BenchAssert.Equal(0xAAAAAAAAu, VramPatterns.Expected(1, VramPatterns.Alternating), "alternating odd");
        BenchAssert.Equal(0xAAAAAAAAu, VramPatterns.Expected(0, VramPatterns.AlternatingInverted), "inverted even");
        BenchAssert.That(VramPatterns.Expected(1, 0) != VramPatterns.Expected(2, 0), "address hash differs per word");
    }

    [Test]
    public void VramPlanClampsAndLeavesReserve()
    {
        const long MB = 1L << 20;
        long gb = _gb;
        BenchAssert.Equal(12 * gb, VramPlan.BytesToTest(24 * gb, 0.5), "half of 24 gb");
        long all = VramPlan.BytesToTest(24 * gb, 1.0);
        BenchAssert.That(all <= (long)(24 * gb * 0.9) && all % MB == 0, "clamped to 90 % and 1 MB aligned");
        BenchAssert.Equal(88 * MB, VramPlan.BytesToTest(600 * MB, 0.5), "512 MB reserve wins");
        BenchAssert.Equal(0L, VramPlan.BytesToTest(400 * MB, 0.5), "below the reserve");
        BenchAssert.Equal(0L, VramPlan.BytesToTest(24 * gb, 0), "zero fraction");
        BenchAssert.Equal(0L, VramPlan.BytesToTest(24 * gb, double.NaN), "NaN fraction");
        BenchAssert.Equal(0L, VramPlan.BytesToTest(-5, 0.5), "negative free");
    }
}
