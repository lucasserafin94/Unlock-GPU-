using UnlockGpu.Core.Oem;

namespace UnlockGpu.Tests;

/// <summary>
/// Tests for the Uniwill EC Data encoding/decoding (pure functions; no WMI, no hardware).
/// <see cref="Run"/> returns the list of failures (empty = all passed).
/// </summary>
public static class OemTests
{
    /// <summary>Runs all OEM tests and returns failure messages.</summary>
    public static IReadOnlyList<string> Run()
    {
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) failures.Add("OemTests: " + name);
        }

        // Encoding: addr16 | data16 << 16 | op16 << 32.
        Check(UniwillEc.EncodeRead(0x0743) == 0x0000_0100_0000_0743UL, "read 0x0743 encodes op 0x0100");
        Check(UniwillEc.EncodeRead(0x0785) == 0x0000_0100_0000_0785UL, "read 0x0785");
        Check(UniwillEc.Encode(0x0744, 0x0050, UniwillEc.OpWrite) == 0x0000_0000_0050_0744UL, "write layout");
        Check(UniwillEc.Encode(0xFFFF, 0xFFFF, 0xFFFF) == 0x0000_FFFF_FFFF_FFFFUL, "all fields max, nothing above bit 47");
        Check(UniwillEc.EncodeRead(0x0751) == 1_099_511_629_649UL, "decimal form sent to WMI for 0x0751");

        // Round trip.
        foreach (var (addr, val, op) in new (ushort, ushort, ushort)[] { (0x0727, 0, 0x0100), (0x0746, 25, 0), (0x1234, 0xABCD, 0x0100) })
            Check(UniwillEc.Decode(UniwillEc.Encode(addr, val, op)) == (addr, val, op), $"round trip 0x{addr:X4}");

        // Return decoding.
        Check(UniwillEc.TryDecodeReadResult(0x0000_0050, out byte v1) && v1 == 0x50, "low byte is value");
        Check(UniwillEc.TryDecodeReadResult(0x1234_56A0, out byte v2) && v2 == 0xA0, "upper bytes ignored");
        Check(!UniwillEc.TryDecodeReadResult(0xFEFE_FEFE, out _), "0xFEFEFEFE is EC error");
        Check(UniwillEc.TryDecodeReadResult(0x0000_00FE, out byte v3) && v3 == 0xFE, "0xFE byte alone is valid");

        // COM may deliver uint32 as a negative int.
        Check(UniwillEc.ToUInt32(unchecked((int)0xFEFEFEFE)) == UniwillEc.EcError, "int -> uint32 keeps bits");
        Check(UniwillEc.ToUInt32("4278124286") == UniwillEc.EcError, "string -> uint32");
        Check(UniwillEc.ToUInt32(80L) == 80u, "long -> uint32");

        // Performance mode (0x0751).
        Check(UniwillEc.DecodePerformanceMode(0xA0) == OemPerformanceMode.PowerSave, "0xA0 power save");
        Check(UniwillEc.DecodePerformanceMode(0x00) == OemPerformanceMode.Enthusiast, "0x00 enthusiast");
        Check(UniwillEc.DecodePerformanceMode(0x10) == OemPerformanceMode.Overboost, "0x10 overboost");
        Check(UniwillEc.DecodePerformanceMode(0x80) == OemPerformanceMode.Unknown, "0x80 unknown");

        // State helpers.
        var s = new OemPowerState { TimeUtc = DateTime.UtcNow, CtgpEnabled = true, CtgpOffsetW = 80, DynamicBoostEnabled = true, DynamicBoostOffsetW = 25 };
        Check(s.ConfiguredTgpW(95) == 175, "cTGP = base + offset");
        Check(s.ConfiguredTgpWithBoostW(95) == 200, "cTGP + DB");
        Check((s with { CtgpEnabled = false }).ConfiguredTgpW(95) is null, "cTGP disabled -> null");

        return failures;
    }
}
