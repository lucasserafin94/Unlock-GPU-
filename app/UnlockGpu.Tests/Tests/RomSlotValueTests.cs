using UnlockGpu.Patch;

namespace UnlockGpu.Tests;

/// <summary>Persistent power card: blob round-trips (width 4 and duplicated width-8) and control→slot role resolution.</summary>
public sealed class RomSlotValueTests
{
    // [5B vBIOS header][int32 LE shadow @5][width @9][payload] — the exact layout NvpwrControl writes.
    static byte[] Blob(int shadow, int width, long value)
    {
        var data = new byte[10 + width];
        data[0] = 0x98; data[1] = 0x03; data[2] = 0x5E; data[3] = 0x00; data[4] = 0x1E; // vBIOS id of the target machine
        Buffer.BlockCopy(BitConverter.GetBytes(shadow), 0, data, 5, 4);
        data[9] = (byte)width;
        var valueBytes = BitConverter.GetBytes((int)value);
        Buffer.BlockCopy(valueBytes, 0, data, 10, Math.Min(4, width));
        if (width == 8) Buffer.BlockCopy(valueBytes, 0, data, 14, 4);
        return data;
    }

    static RomSlotInfo Slot(int index, int shadow, int width, long value) =>
        new(index, 10 + width, "", shadow, width, value);

    [Test]
    public void Width4_RoundTrip_PreservesHeaderShadowAndChangesValue()
    {
        var original = Blob(0x57C36, 4, 250_000); // BOARD_MAX @ shadow 0x57C36, 250 W in mW

        Check.True(RomSlot.TryParseBlob(original, out var parsed));
        Check.Equal(0x57C36, parsed.ShadowOffset);
        Check.Equal(4, parsed.Width);
        Check.Equal(250_000L, RomSlot.BlobValue(parsed));

        Check.True(RomSlot.BuildValueBlob(parsed, 180_000, out var rebuilt));
        Check.True(RomSlot.TryParseBlob(rebuilt, out var reparsed));
        Check.Equal(180_000L, RomSlot.BlobValue(reparsed));

        // Header (vBIOS id) and shadow offset are preserved byte-for-byte.
        for (int i = 0; i < 9; i++) Check.Equal(original[i], rebuilt[i]);
    }

    [Test]
    public void Width8_RoundTrip_WritesValueToBothHalves()
    {
        var original = Blob(0x580FA, 8, 71_000); // board-input pair @ shadow 0x580FA, 71 A in mA

        Check.True(RomSlot.TryParseBlob(original, out var parsed));
        Check.Equal(8, parsed.Width);
        Check.Equal(71_000L, RomSlot.BlobValue(parsed));

        Check.True(RomSlot.BuildValueBlob(parsed, 95_000, out var rebuilt));
        Check.True(RomSlot.TryParseBlob(rebuilt, out var reparsed));

        // Both int32 halves of the duplicated pair become the new value.
        Check.Equal(95_000L, BitConverter.ToInt32(rebuilt, 10));
        Check.Equal(95_000L, BitConverter.ToInt32(rebuilt, 14));
        Check.Equal(95_000L, RomSlot.BlobValue(reparsed));
    }

    [Test]
    public void BuildValueBlob_RejectsUnsupportedWidth()
    {
        var original = Blob(0x1000, 2, 42);
        Check.True(RomSlot.TryParseBlob(original, out var parsed));
        Check.False(RomSlot.BuildValueBlob(parsed, 7, out _));
    }

    [Test]
    public void ResolveRoles_KnownOffsets_MapToCaptureSlots()
    {
        // Same layout as the target machine's capture: MAX/BASE/NVVDD/MSVDD at known shadows + width-8 board input.
        var slots = new List<RomSlotInfo>
        {
            Slot(0, 0x57C36, 4, 250_000),
            Slot(1, 0x57C6A, 4, 250_000),
            Slot(2, 0x57F00, 4, 15_001),
            Slot(3, 0x57EFC, 4, 15_001),
            Slot(4, 0x580FA, 8, 71_000),
        };

        var roles = RomOverrideService.ResolveRoles(slots);
        Check.True(roles.Any);
        Check.Equal(0, roles.MaxSlot);
        Check.Equal(1, roles.BaseSlot);
        Check.Equal(2, roles.NvvddSlot);
        Check.Equal(3, roles.MsvddSlot);
        Check.Equal(4, roles.BoardInputSlot);
    }

    [Test]
    public void ResolveRoles_UnknownOffsets_FallsBackToDenseOrder()
    {
        // A board with the same dense slot order but different shadows: 0=MAX, 1=BASE, 2/3=rail OCPs.
        var slots = new List<RomSlotInfo>
        {
            Slot(0, 0x40000, 4, 175_000),
            Slot(1, 0x40034, 4, 175_000),
            Slot(2, 0x40100, 4, 12_000),
            Slot(3, 0x400FC, 4, 12_000),
        };

        var roles = RomOverrideService.ResolveRoles(slots);
        Check.True(roles.Any);
        Check.Equal(0, roles.MaxSlot);
        Check.Equal(1, roles.BaseSlot);
        Check.Equal(2, roles.NvvddSlot);
        Check.Equal(3, roles.MsvddSlot);
        Check.True(roles.BoardInputSlot is null); // no width-8 slot → board input unavailable
    }
}
