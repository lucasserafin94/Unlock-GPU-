using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>In-memory GPU for tests: scripted samples, offsets with ranges, configurable return codes, call counters.</summary>
public sealed class FakeGpu : IGpu, IGpuInitGeneration
{
    readonly Dictionary<ClockDomain, OffsetInfo> _offsets = new()
    {
        [ClockDomain.Core] = new OffsetInfo(0, -1000, 1000),
        [ClockDomain.Memory] = new OffsetInfo(0, -2000, 6000),
    };

    public bool IsAvailable { get; set; } = true;
    public string LastError { get; set; } = "";
    public GpuInfo? Info { get; set; } = new("NVIDIA GeForce RTX 5090 Laptop GPU", "621.35", "98.02.00.00.00", 55, 95, 175, 3090,
        [14001, 11001, 9001, 810, 405]);

    /// <summary>Returned by <see cref="Sample"/> when the queue is empty.</summary>
    public GpuSample NextSample { get; set; } = Idle("P8", 405);
    public Queue<GpuSample> Samples { get; } = new();
    public int SampleCalls { get; private set; }

    public double EnforcedW { get; set; } = 175;
    /// <summary>Configured limit; NaN simulates a driver that does not expose it (like the RTX 5090 Laptop tested).</summary>
    public double ConfiguredW { get; set; } = double.NaN;
    public int SetPowerRc { get; set; }
    public int SetPowerCalls { get; private set; }

    public int SetOffsetRc { get; set; }
    public int SetOffsetCalls { get; private set; }
    /// <summary>Optional transform simulating the driver storing a different value than requested.</summary>
    public Func<ClockDomain, int, int>? OffsetStored { get; set; }
    public bool OffsetsReadable { get; set; } = true;

    public int LockRc { get; set; }
    public int LockCalls { get; private set; }
    public int ResetCalls { get; private set; }
    public uint? LockedMHz { get; private set; }

    public int InitGeneration { get; set; } = 1;

    public static GpuSample Idle(string pstate, uint memMHz, uint util = 0) => new()
    {
        Time = DateTime.Now, Ok = true, PState = pstate, MemMHz = memMHz, CoreMHz = 210, UtilGpu = util, PowerW = 12, LimitW = 175, TempC = 45,
    };

    public static GpuSample Load(double powerW, uint coreMHz, uint memMHz, ClockReasons reasons = ClockReasons.None, uint util = 99, uint temp = 75) => new()
    {
        Time = DateTime.Now, Ok = true, PState = "P0", MemMHz = memMHz, CoreMHz = coreMHz, SmMHz = coreMHz, UtilGpu = util,
        PowerW = powerW, LimitW = 175, TempC = temp, Reasons = reasons,
    };

    public GpuSample Sample()
    {
        SampleCalls++;
        return Samples.Count > 0 ? Samples.Dequeue() : NextSample;
    }

    public double EnforcedLimitW() => EnforcedW;
    public bool ConfiguredUnreadable { get; set; }
    public double ConfiguredLimitW() => ConfiguredUnreadable ? double.NaN : double.IsNaN(ConfiguredW) ? EnforcedW : ConfiguredW;

    public int SetPowerLimitW(double watts)
    {
        SetPowerCalls++;
        if (SetPowerRc == 0) { EnforcedW = watts; if (!double.IsNaN(ConfiguredW)) ConfiguredW = watts; }
        return SetPowerRc;
    }

    public OffsetInfo? GetOffset(ClockDomain domain) => OffsetsReadable ? _offsets[domain] : null;

    /// <summary>Sets the current offset directly (simulates driver/other tool).</summary>
    public void ForceOffset(ClockDomain domain, int mhz) => _offsets[domain] = _offsets[domain] with { CurrentMHz = mhz };

    public int SetOffset(ClockDomain domain, int mhz)
    {
        SetOffsetCalls++;
        if (SetOffsetRc != 0) return SetOffsetRc;
        ForceOffset(domain, OffsetStored?.Invoke(domain, mhz) ?? mhz);
        return 0;
    }

    public int LockMemoryClock(uint mhz)
    {
        LockCalls++;
        if (LockRc == 0) LockedMHz = mhz;
        return LockRc;
    }

    public int ResetMemoryClock()
    {
        ResetCalls++;
        LockedMHz = null;
        return 0;
    }

    public string ErrorText(int rc) => rc switch { 3 => "Not Supported (3)", 4 => "Insufficient Permissions (4)", _ => $"Error ({rc})" };

    public void Dispose() { }
}
