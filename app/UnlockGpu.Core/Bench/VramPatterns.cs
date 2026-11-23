using System.Numerics;

namespace UnlockGpu.Core.Bench;

/// <summary>Test patterns written by the VRAM fill shader and verified on the CPU. Pure functions (unit-testable).</summary>
public static class VramPatterns
{
    /// <summary>Address hash: every word differs from its neighbours (catches address/aliasing faults).</summary>
    public const uint AddressHash = 0;
    /// <summary>0x55555555 / 0xAAAAAAAA alternating per word.</summary>
    public const uint Alternating = 1;
    /// <summary>The inverse of <see cref="Alternating"/>, so every bit is checked as both 0 and 1.</summary>
    public const uint AlternatingInverted = 2;
    /// <summary>Number of passes the tester runs (one per pattern).</summary>
    public const uint Count = 3;

    const uint HashMultiplier = 0x9E3779B1u;

    /// <summary>Expected 32-bit word at global word address <paramref name="address"/> (mod 2^32).</summary>
    public static uint Expected(uint address, uint pattern)
    {
        if (pattern == AddressHash)
        {
            uint x = unchecked(address * HashMultiplier);
            return x ^ (x >> 16);
        }
        uint alt = (address & 1u) != 0 ? 0xAAAAAAAAu : 0x55555555u;
        return pattern == Alternating ? alt : ~alt;
    }

    /// <summary>Counts words in <paramref name="data"/> that differ from the pattern, where data[0] sits at
    /// <paramref name="baseAddress"/>. Vectorised; a mismatching block falls back to a per-lane count.</summary>
    public static long CountMismatches(ReadOnlySpan<uint> data, uint baseAddress, uint pattern)
    {
        long errors = 0;
        int lanes = Vector<uint>.Count;
        int i = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= lanes)
        {
            var sequence = Vector.CreateSequence(0u, 1u);
            var multiplier = new Vector<uint>(HashMultiplier);
            Span<uint> constLanes = stackalloc uint[lanes];
            for (int l = 0; l < lanes; l++) constLanes[l] = Expected(baseAddress + (uint)l, pattern);
            var constant = new Vector<uint>(constLanes); // alternating patterns repeat every 2 words and lanes is even
            for (; i + lanes <= data.Length; i += lanes)
            {
                Vector<uint> expected;
                if (pattern == AddressHash)
                {
                    var x = (new Vector<uint>(unchecked(baseAddress + (uint)i)) + sequence) * multiplier;
                    expected = x ^ Vector.ShiftRightLogical(x, 16);
                }
                else expected = constant;
                var actual = new Vector<uint>(data.Slice(i, lanes));
                if (Vector.EqualsAll(actual, expected)) continue;
                for (int l = 0; l < lanes; l++)
                    if (actual[l] != expected[l]) errors++;
            }
        }
        for (; i < data.Length; i++)
            if (data[i] != Expected(unchecked(baseAddress + (uint)i), pattern)) errors++;
        return errors;
    }
}

/// <summary>How much VRAM a test may take. Pure function (unit-testable).</summary>
public static class VramPlan
{
    /// <summary>Memory always left free for the desktop and other apps.</summary>
    public const long ReserveBytes = 512L << 20;
    /// <summary>Upper bound as a fraction of the free memory.</summary>
    public const double MaxFraction = 0.9;
    const long Granularity = 1L << 20;

    /// <summary>Bytes to allocate: <paramref name="fractionOfFree"/> of <paramref name="freeBytes"/>, never above 90 % of it
    /// and always leaving <see cref="ReserveBytes"/> free; rounded down to 1 MB. 0 means "not enough memory".</summary>
    public static long BytesToTest(long freeBytes, double fractionOfFree)
    {
        if (freeBytes <= 0 || double.IsNaN(fractionOfFree) || fractionOfFree <= 0) return 0;
        double fraction = Math.Min(fractionOfFree, MaxFraction);
        long bytes = Math.Min((long)(freeBytes * fraction), freeBytes - ReserveBytes);
        return bytes <= 0 ? 0 : bytes / Granularity * Granularity;
    }
}
