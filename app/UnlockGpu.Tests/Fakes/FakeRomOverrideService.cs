using UnlockGpu.Core.Model;
using UnlockGpu.Patch;

namespace UnlockGpu.Tests;

/// <summary>Slot layouts for tests, mirroring the shapes the driver key can hold.</summary>
public static class FakeRomLayout
{
    /// <summary>One slot row (size = 10 + payload width, like the real blobs).</summary>
    public static RomSlotInfo Slot(int index, int shadow, int width, long value) => new(index, 10 + width, "", shadow, width, value);

    /// <summary>Same layout as the target board's capture: MAX/BASE/rails at known shadows + a width-8 board input.</summary>
    public static IReadOnlyList<RomSlotInfo> KnownBoard(long maxRaw = 250_000, long baseRaw = 225_000) =>
    [
        Slot(0, RomRoleOffsets.Max, 4, maxRaw),
        Slot(1, RomRoleOffsets.Base, 4, baseRaw),
        Slot(2, RomRoleOffsets.Nvvdd, 4, 15_001),
        Slot(3, RomRoleOffsets.Msvdd, 4, 15_001),
        Slot(4, 0x580FA, 8, 71_000),
    ];

    /// <summary>Dense fallback layout: width-4 slots at unknown shadows (no board-input pair).</summary>
    public static IReadOnlyList<RomSlotInfo> DenseFallback() =>
    [
        Slot(0, 0x40000, 4, 175_000),
        Slot(1, 0x40034, 4, 175_000),
        Slot(2, 0x40100, 4, 12_000),
        Slot(3, 0x400FC, 4, 12_000),
    ];
}

/// <summary>
/// In-memory driver-key slots: SetSlotValue/ClearAll mutate the stored raw values so a later ReadSlots sees them,
/// like the real registry key. Records the expected max passed to HotRestartAsync for assertions.
/// </summary>
public sealed class FakeRomOverrideService : IRomOverrideService
{
    readonly Dictionary<int, (int Shadow, int Width, long Raw)> _slots = new();

    /// <summary>PnP ID returned by FindGpuPnpId; null simulates "no NVIDIA GPU found".</summary>
    public string? PnpId { get; set; } = "PCI\\VEN_10DE&DEV_2C18";

    /// <summary>Every SetSlotValue call, in order.</summary>
    public List<(int Slot, long Raw)> Writes { get; } = [];

    /// <summary>How many times ReadSlots ran (cross-tab sync assertions).</summary>
    public int ReadSlotCalls { get; private set; }

    /// <summary>The expectedMaxW of the last HotRestartAsync call.</summary>
    public double? LastHotRestartExpected { get; private set; }

    /// <summary>Scripted initial layout (index → shadow/width/raw); later reads reflect writes and clears.</summary>
    public IReadOnlyList<RomSlotInfo> Slots
    {
        get => _slots.OrderBy(kv => kv.Key).Select(kv => FakeRomLayout.Slot(kv.Key, kv.Value.Shadow, kv.Value.Width, kv.Value.Raw)).ToList();
        set
        {
            _slots.Clear();
            foreach (var s in value) if (s.RawValue is long raw) _slots[s.Index] = (s.ShadowOffset, s.PayloadWidth, raw);
        }
    }

    public string? FindGpuPnpId() => PnpId;

    public IReadOnlyList<RomSlotInfo> ReadSlots(string pnpInstanceId, out string error)
    {
        ReadSlotCalls++;
        error = "";
        return Slots;
    }

    public ApplyResult StageCaptured(string pnpInstanceId, string captureFile) => new(true, "stage", "");

    public ApplyResult ClearAll(string pnpInstanceId)
    {
        _slots.Clear();
        return new ApplyResult(true, "clear", "");
    }

    public ApplyResult SetSlotValue(string pnpInstanceId, int slotIndex, long rawValue)
    {
        Writes.Add((slotIndex, rawValue));
        if (_slots.TryGetValue(slotIndex, out var s)) _slots[slotIndex] = (s.Shadow, s.Width, rawValue);
        return new ApplyResult(true, "set slot value", $"slot {slotIndex} verified");
    }

    public Task<ApplyResult> HotRestartAsync(string pnpInstanceId, double? expectedMaxW, bool force = false, CancellationToken ct = default)
    {
        LastHotRestartExpected = expectedMaxW;
        return Task.FromResult(new ApplyResult(true, "hot restart", ""));
    }
}
