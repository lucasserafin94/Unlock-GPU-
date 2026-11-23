namespace UnlockGpu.Core.Oem;

/// <summary>
/// Uniwill EC protocol over WMI class AcpiTest_MULong, method GetSetULong(uint64 Data) -&gt; uint32.
/// Source: Linux Documentation/wmi/devices/uniwill-laptop.rst and tuxedo-drivers (public).
/// Data = address (bits 0-15) | value (bits 16-31) | operation (bits 32-47). Pure functions, no I/O.
/// </summary>
public static class UniwillEc
{
    /// <summary>WMI namespace of the EC class.</summary>
    public const string WmiNamespace = @"root\wmi";
    /// <summary>WMI class name.</summary>
    public const string WmiClass = "AcpiTest_MULong";
    /// <summary>WMI method name.</summary>
    public const string WmiMethod = "GetSetULong";
    /// <summary>WMI GUID of the class (_WDG).</summary>
    public const string WmiGuid = "ABBC0F6F-8EA1-11D1-00A0-C90629100000";

    /// <summary>Operation code for an EC read.</summary>
    public const ushort OpRead = 0x0100;
    /// <summary>Operation code for an EC write (not used in this build).</summary>
    public const ushort OpWrite = 0x0000;
    /// <summary>Return value that signals an EC error.</summary>
    public const uint EcError = 0xFEFEFEFE;

    /// <summary>Known EC RAM registers.</summary>
    public static class Reg
    {
        /// <summary>bit6 custom profile mode, bit7 PL4 doubled.</summary>
        public const ushort ProfileFlags = 0x0727;
        /// <summary>bit0 general, bit1 Dynamic Boost enable, bit2 cTGP enable.</summary>
        public const ushort GpuControl = 0x0743;
        /// <summary>cTGP offset (W above base TGP).</summary>
        public const ushort CtgpOffset = 0x0744;
        /// <summary>TPP offset (W).</summary>
        public const ushort TppOffset = 0x0745;
        /// <summary>Dynamic Boost offset (W, max 25).</summary>
        public const ushort DynamicBoostOffset = 0x0746;
        /// <summary>Performance / fan mode.</summary>
        public const ushort PerformanceMode = 0x0751;
        /// <summary>CPU PL1 (W).</summary>
        public const ushort CpuPl1 = 0x0783;
        /// <summary>CPU PL2 (W).</summary>
        public const ushort CpuPl2 = 0x0784;
        /// <summary>CPU PL4 (W, doubled when ProfileFlags bit7).</summary>
        public const ushort CpuPl4 = 0x0785;
    }

    /// <summary>Builds the Data argument.</summary>
    public static ulong Encode(ushort address, ushort value, ushort operation) =>
        address | ((ulong)value << 16) | ((ulong)operation << 32);

    /// <summary>Data argument for reading <paramref name="address"/>.</summary>
    public static ulong EncodeRead(ushort address) => Encode(address, 0, OpRead);

    /// <summary>True only for a well-formed read request (operation <see cref="OpRead"/>, value 0, upper bits clear).</summary>
    public static bool IsRead(ulong data)
    {
        var (_, value, operation) = Decode(data);
        return operation == OpRead && value == 0 && (data >> 48) == 0;
    }

    /// <summary>Splits a Data argument back into its fields.</summary>
    public static (ushort Address, ushort Value, ushort Operation) Decode(ulong data) =>
        ((ushort)(data & 0xFFFF), (ushort)((data >> 16) & 0xFFFF), (ushort)((data >> 32) & 0xFFFF));

    /// <summary>Extracts the register byte from a GetSetULong return value. False on EC error.</summary>
    public static bool TryDecodeReadResult(uint result, out byte value)
    {
        value = (byte)(result & 0xFF);
        return result != EcError;
    }

    /// <summary>Decodes register 0x0751. 0xA0 power save, 0x00 enthusiast, 0x10 (bit4 TBME) overboost.</summary>
    public static OemPerformanceMode DecodePerformanceMode(byte raw)
    {
        if ((raw & 0xA0) == 0xA0) return OemPerformanceMode.PowerSave;
        if ((raw & 0x10) != 0) return OemPerformanceMode.Overboost;
        if ((raw & 0xB0) == 0) return OemPerformanceMode.Enthusiast;
        return OemPerformanceMode.Unknown;
    }

    /// <summary>Localizer key for a performance mode.</summary>
    public static string ModeKey(OemPerformanceMode mode) => mode switch
    {
        OemPerformanceMode.PowerSave => "oem.mode.powerSave",
        OemPerformanceMode.Enthusiast => "oem.mode.enthusiast",
        OemPerformanceMode.Overboost => "oem.mode.overboost",
        _ => "oem.mode.unknown",
    };

    /// <summary>Converts a WMI uint32 value (which COM may deliver as int, long, uint or string) to uint.</summary>
    public static uint ToUInt32(object? value) => value switch
    {
        uint u => u,
        int i => unchecked((uint)i),
        long l => unchecked((uint)l),
        ulong ul => unchecked((uint)ul),
        short s => unchecked((uint)s),
        ushort us => us,
        byte b => b,
        string str => uint.Parse(str, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture),
        null => throw new InvalidOperationException("WMI returned no value."),
        _ => throw new InvalidOperationException("Unexpected WMI value type: " + value.GetType().Name),
    };
}
