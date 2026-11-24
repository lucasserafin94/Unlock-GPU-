using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>In-memory clock offsets with fixed driver ranges.</summary>
public sealed class FakeOffsetService : IOffsetService
{
    public OffsetInfo? Get(ClockDomain domain) => domain switch
    {
        ClockDomain.Core => new(0, -1000, 1000),
        ClockDomain.Memory => new(0, -2000, 6000),
        _ => null,
    };

    public ApplyResult Apply(int coreMHz, int memMHz) => new(true, "offsets", $"core {coreMHz} / mem {memMHz} MHz");
}

/// <summary>In-memory VRAM lock with two levels.</summary>
public sealed class FakeVramLockService : IVramLockService
{
    public IReadOnlyList<uint> Levels { get; } = [14001u, 11001u];
    public ApplyResult Lock(uint levelMHz) => new(true, "lock", $"{levelMHz} MHz");
    public ApplyResult Unlock() => new(true, "unlock", "auto clocks");
    public VramLockState Evaluate(GpuSample sample) => new(false, null, 0, false);
}

/// <summary>In-memory power limit service (adjustable by default).</summary>
public sealed class FakePowerService : IPowerService
{
    public PowerSupport Support { get; set; } = PowerSupport.Adjustable;
    public PowerSupport Probe() => Support;
    public ApplyResult Apply(double watts) => new(true, "power", $"{watts:0} W");
}

/// <summary>In-memory settings store that records every save.</summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Current { get; set; } = new();
    public List<AppSettings> Saved { get; } = [];
    public void Save(AppSettings settings) { Current = settings; Saved.Add(settings); Changed?.Invoke(this, settings); }
    public event EventHandler<AppSettings>? Changed;
}
