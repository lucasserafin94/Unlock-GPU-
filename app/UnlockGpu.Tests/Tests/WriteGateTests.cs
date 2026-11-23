using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>While the auto-tuner holds the gate, writes from any other flow are refused before reaching the GPU.</summary>
public sealed class WriteGateTests
{
    readonly MemoryLog Log = new();

    [Test]
    public void ManualOffset_IsRefused_WhileTunerHoldsGate_AndAllowedAfter()
    {
        using var gpu = new FakeGpu();
        var gate = new WriteGate();
        var offsets = new OffsetService(gpu, Log, gate: gate);
        int before = gpu.SetOffsetCalls;

        var tunerReady = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var tuner = Task.Run(() =>
        {
            using (gate.EnterTuner())
            {
                Check.True(offsets.Apply(90, 0).Ok);   // the tuner's own flow may write
                tunerReady.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        });

        Check.True(tunerReady.Wait(TimeSpan.FromSeconds(5)));
        var refused = offsets.Apply(0, 500);           // a different flow (UI, tray, reapply)
        Check.False(refused.Ok);
        Check.Equal(ServiceTextKeys.TunerBusy, ApplyError.Split(refused.ErrorText).Key);
        Check.Equal(before + 1, gpu.SetOffsetCalls);   // only the tuner's write reached the GPU

        release.Set();
        tuner.Wait(TimeSpan.FromSeconds(5));
        Check.False(gate.TunerActive);
        Check.True(offsets.Apply(0, 0).Ok);
    }

    [Test]
    public void SecondTuner_CannotEnter()
    {
        var gate = new WriteGate();
        using (gate.EnterTuner())
        {
            bool threw = false;
            try { gate.EnterTuner().Dispose(); } catch (InvalidOperationException) { threw = true; }
            Check.True(threw);
        }
        Check.False(gate.TunerActive);
    }

    [Test]
    public void EnterTuner_WaitsForInFlightWrite_FromAnotherFlow()
    {
        var gate = new WriteGate();
        var writing = new ManualResetEventSlim();
        var finish = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            Check.True(gate.TryBeginWrite());          // a manual write already in progress
            writing.Set();
            finish.Wait(TimeSpan.FromSeconds(5));
            gate.EndWrite();
        });
        Check.True(writing.Wait(TimeSpan.FromSeconds(5)));

        var entered = new ManualResetEventSlim();
        var tuner = Task.Run(() =>
        {
            using (gate.EnterTuner())
            {
                entered.Set();
                Check.True(gate.TryBeginWrite());      // the tuner's own writes are not blocked
                gate.EndWrite();
            }
        });

        Check.False(entered.Wait(TimeSpan.FromMilliseconds(300)));   // still waiting for the manual write
        Check.True(gate.TunerActive);
        Check.False(gate.TryBeginWrite());             // new writes from other flows are refused meanwhile

        finish.Set();
        Check.True(writer.Wait(TimeSpan.FromSeconds(5)));
        Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Check.True(tuner.Wait(TimeSpan.FromSeconds(5)));
        Check.Equal(0, gate.InFlightWrites);
        Check.False(gate.TunerActive);
    }
}
