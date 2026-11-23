using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

public sealed class VramLockServiceTests : TempDirTest
{
    readonly FakeGpu _gpu = new();
    readonly ManualTime _time = new();
    DateTime _boot = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);

    VramLockService Create() => new(_gpu, Paths, Log, _time, () => _boot);

    [Test]
    public void Levels_AreTheTwoHighestMemoryClocks()
    {
        var levels = Create().Levels;
        Check.Equal(2, levels.Count);
        Check.Equal(14001u, levels[0]);
        Check.Equal(11001u, levels[1]);
    }

    [Test]
    public void Lock_InvalidLevel_IsRejectedWithoutCallingDriver()
    {
        var r = Create().Lock(9001);
        Check.False(r.Ok);
        Check.Equal(ServiceTextKeys.VramInvalidLevel, ApplyError.Split(r.ErrorText).Key);
        Check.Equal(0, _gpu.LockCalls);
    }

    [Test]
    public void Lock_DriverRefusal_ReportsAndKeepsStateUnlocked()
    {
        _gpu.LockRc = 4;
        var svc = Create();
        var r = svc.Lock(14001);
        Check.False(r.Ok);
        Check.Equal(ServiceTextKeys.VramDriver, ApplyError.Split(r.ErrorText).Key);
        var st = svc.Evaluate(FakeGpu.Idle("P8", 405));
        Check.False(st.Locked);
        Check.False(st.Lost);
    }

    [Test]
    public void Detection_WithoutState_NeedsThreeIdleConfirmations()
    {
        var svc = Create();
        Check.False(svc.Evaluate(FakeGpu.Idle("P0", 15001)).Locked);
        Check.False(svc.Evaluate(FakeGpu.Idle("P0", 15001)).Locked);
        var third = svc.Evaluate(FakeGpu.Idle("P0", 15001));
        Check.True(third.Locked);
        Check.Equal(14001u, third.LevelMHz);
        Check.Equal(1000, third.OffsetMHz);

        // Under load the last detection is kept.
        var load = svc.Evaluate(FakeGpu.Load(150, 2500, 15001));
        Check.True(load.Locked);
        Check.Equal(14001u, load.LevelMHz);

        // Idle in a low P-state: not locked; nothing was applied by the app, so not "lost".
        var idle = svc.Evaluate(FakeGpu.Idle("P8", 405));
        Check.False(idle.Locked);
        Check.False(idle.Lost);
    }

    [Test]
    public void Detection_CounterResetsWhenGpuIdlesDown()
    {
        var svc = Create();
        svc.Evaluate(FakeGpu.Idle("P0", 14001));
        svc.Evaluate(FakeGpu.Idle("P0", 14001));
        svc.Evaluate(FakeGpu.Idle("P8", 405));
        Check.False(svc.Evaluate(FakeGpu.Idle("P0", 14001)).Locked);
    }

    [Test]
    public void Detection_HighUtilisationDoesNotCount()
    {
        var svc = Create();
        for (int i = 0; i < 5; i++) Check.False(svc.Evaluate(FakeGpu.Idle("P0", 14001, util: 40)).Locked);
    }

    [Test]
    public void Lock_StateMakesDetectionImmediate_OnSecondLevel()
    {
        var svc = Create();
        Check.True(svc.Lock(11001).Ok);
        Check.Equal(11001u, _gpu.LockedMHz);
        var st = svc.Evaluate(FakeGpu.Idle("P2", 12001));
        Check.True(st.Locked);
        Check.Equal(11001u, st.LevelMHz);
        Check.Equal(1000, st.OffsetMHz);
    }

    [Test]
    public void Lock_GraceThenLost()
    {
        var svc = Create();
        Check.True(svc.Lock(14001).Ok);

        _time.Advance(TimeSpan.FromSeconds(5));
        var inGrace = svc.Evaluate(FakeGpu.Idle("P8", 405));
        Check.True(inGrace.Locked);
        Check.Equal(14001u, inGrace.LevelMHz);
        Check.Equal(null, inGrace.OffsetMHz);

        _time.Advance(TimeSpan.FromSeconds(6));
        var lost = svc.Evaluate(FakeGpu.Idle("P8", 405));
        Check.False(lost.Locked);
        Check.True(lost.Lost);
    }

    [Test]
    public void State_IsSharedBetweenInstances_OfTheSameBoot()
    {
        Check.True(Create().Lock(14001).Ok);   // e.g. the silent startup process
        var ui = Create();                       // the UI process later
        var st = ui.Evaluate(FakeGpu.Load(160, 2400, 14501));
        Check.True(st.Locked);
        Check.Equal(14001u, st.LevelMHz);
        Check.Equal(500, st.OffsetMHz);
    }

    [Test]
    public void State_FromPreviousBoot_IsIgnored()
    {
        Check.True(Create().Lock(14001).Ok);
        _boot = _boot.AddHours(5);
        var st = Create().Evaluate(FakeGpu.Load(160, 2400, 9001));
        Check.False(st.Locked);
        Check.False(Create().Evaluate(FakeGpu.Idle("P8", 405)).Lost);
    }

    [Test]
    public void Unlock_ClearsDetectionAndState()
    {
        var svc = Create();
        svc.Lock(14001);
        Check.True(svc.Unlock().Ok);
        Check.Equal(1, _gpu.ResetCalls);
        var st = svc.Evaluate(FakeGpu.Idle("P8", 405));
        Check.False(st.Locked);
        Check.False(st.Lost);
    }

    [Test]
    public void FailedSample_KeepsLastKnownState()
    {
        var svc = Create();
        svc.Lock(14001);
        var st = svc.Evaluate(new Core.Model.GpuSample { Time = DateTime.Now, Ok = false });
        Check.True(st.Locked);
        Check.Equal(14001u, st.LevelMHz);
    }

    [Test]
    public void NoLevels_LockFails()
    {
        _gpu.Info = _gpu.Info! with { MemClocks = [] };
        var r = Create().Lock(14001);
        Check.Equal(ServiceTextKeys.VramNoLevels, ApplyError.Split(r.ErrorText).Key);
        Check.Equal(0, _gpu.LockCalls);
    }

    [Test]
    public void AppPathsCreatesFolders() => Check.True(Directory.Exists(new AppPaths(Path.Combine(Root, "x")).ProfilesDir));
}
