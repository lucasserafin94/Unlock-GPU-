namespace UnlockGpu.Core.Oem;

/// <summary>
/// Power settings that live in the laptop's embedded controller (EC), reached through the OEM's public WMI
/// interface. This build is read-only: <see cref="Apply"/> always throws <see cref="NotSupportedException"/>.
/// </summary>
public interface IOemPowerService
{
    /// <summary>Checks vendor/board and whether the OEM WMI class is present and callable. Never writes anything.</summary>
    OemDetection Detect();

    /// <summary>
    /// Reads mode, cTGP/Dynamic Boost control and offsets, and CPU PL1/PL2/PL4 from the EC (read operations only).
    /// Registers that fail to read are null in the result and listed in <see cref="OemPowerState.FailedRegisters"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The process is not elevated (WMI access denied).</exception>
    /// <exception cref="InvalidOperationException">The OEM WMI interface is not available on this machine.</exception>
    OemPowerState ReadState(CancellationToken ct = default);

    /// <summary>Reserved for a later, separately reviewed build. Always throws NotSupportedException("oem.writeDisabled").</summary>
    OemApplyResult Apply(OemPowerRequest request);
}

/// <summary>Outcome of <see cref="IOemPowerService.Detect"/>.</summary>
public enum OemStatus
{
    /// <summary>Uniwill board and the EC WMI interface answered.</summary>
    Available,
    /// <summary>Vendor/board is not a known Uniwill design.</summary>
    NotUniwill,
    /// <summary>Board looks like Uniwill but the AcpiTest_MULong WMI class is missing.</summary>
    WmiClassMissing,
    /// <summary>The class exists but calling it needs administrator rights.</summary>
    AccessDenied,
    /// <summary>WMI failed for another reason.</summary>
    Error,
}

/// <summary>SMBIOS identity strings used to recognise the platform.</summary>
public sealed record SystemIdentity(string SystemVendor, string SystemProduct, string BoardVendor, string BoardProduct)
{
    /// <summary>Identity with all fields empty (used when WMI cannot be read).</summary>
    public static SystemIdentity Unknown { get; } = new("", "", "", "");
}

/// <summary>Detection result. <paramref name="MessageKey"/> is a Localizer key describing the status.</summary>
/// <param name="Status">Overall status.</param>
/// <param name="Identity">SMBIOS identity that was read.</param>
/// <param name="BoardVerified">True only for boards whose register map was checked on real hardware.</param>
/// <param name="MessageKey">Localizer key for the user-facing explanation.</param>
public sealed record OemDetection(OemStatus Status, SystemIdentity Identity, bool BoardVerified, string MessageKey);

/// <summary>EC performance (fan/power) mode, register 0x0751.</summary>
public enum OemPerformanceMode { Unknown, PowerSave, Enthusiast, Overboost }

/// <summary>
/// Snapshot of the EC power registers. Every value is nullable: null means that register could not be read.
/// Offsets are in watts as stored by the EC (cTGP offset is above the GPU base TGP).
/// </summary>
public sealed record OemPowerState
{
    /// <summary>When the snapshot was taken (UTC).</summary>
    public required DateTime TimeUtc { get; init; }
    /// <summary>Raw 0x0751 byte.</summary>
    public byte? PerformanceModeRaw { get; init; }
    /// <summary>Decoded 0x0751.</summary>
    public OemPerformanceMode PerformanceMode { get; init; }
    /// <summary>Raw 0x0743 byte (bit0 general, bit1 Dynamic Boost, bit2 cTGP).</summary>
    public byte? ControlRaw { get; init; }
    /// <summary>0x0743 bit0.</summary>
    public bool? GeneralEnabled { get; init; }
    /// <summary>0x0743 bit1.</summary>
    public bool? DynamicBoostEnabled { get; init; }
    /// <summary>0x0743 bit2.</summary>
    public bool? CtgpEnabled { get; init; }
    /// <summary>0x0744: configurable TGP offset in W above base TGP.</summary>
    public int? CtgpOffsetW { get; init; }
    /// <summary>0x0745: total processing power (TPP) offset in W.</summary>
    public int? TppOffsetW { get; init; }
    /// <summary>0x0746: Dynamic Boost offset in W (firmware maximum 25).</summary>
    public int? DynamicBoostOffsetW { get; init; }
    /// <summary>Raw 0x0727 byte.</summary>
    public byte? ProfileFlagsRaw { get; init; }
    /// <summary>0x0727 bit6: custom profile mode.</summary>
    public bool? CustomProfileMode { get; init; }
    /// <summary>0x0727 bit7: PL4 register value is doubled.</summary>
    public bool? Pl4Doubled { get; init; }
    /// <summary>0x0783: CPU PL1 in W.</summary>
    public int? CpuPl1W { get; init; }
    /// <summary>0x0784: CPU PL2 in W.</summary>
    public int? CpuPl2W { get; init; }
    /// <summary>0x0785: CPU PL4 in W (already doubled when <see cref="Pl4Doubled"/> is true).</summary>
    public int? CpuPl4W { get; init; }
    /// <summary>Registers that returned an EC error or failed to read.</summary>
    public IReadOnlyList<ushort> FailedRegisters { get; init; } = [];

    /// <summary>Configured GPU TGP (base + cTGP offset) when cTGP is enabled; null otherwise.</summary>
    public double? ConfiguredTgpW(double baseTgpW) =>
        CtgpEnabled == true && CtgpOffsetW is int off ? baseTgpW + off : null;

    /// <summary>Configured TGP plus Dynamic Boost when both are enabled; null otherwise.</summary>
    public double? ConfiguredTgpWithBoostW(double baseTgpW) =>
        ConfiguredTgpW(baseTgpW) is double tgp
            ? tgp + (DynamicBoostEnabled == true && DynamicBoostOffsetW is int db ? db : 0)
            : null;
}

/// <summary>Requested EC changes for a future build. Null = leave unchanged.</summary>
public sealed record OemPowerRequest
{
    /// <summary>Performance mode to set.</summary>
    public OemPerformanceMode? PerformanceMode { get; init; }
    /// <summary>cTGP offset in W.</summary>
    public int? CtgpOffsetW { get; init; }
    /// <summary>Dynamic Boost offset in W.</summary>
    public int? DynamicBoostOffsetW { get; init; }
}

/// <summary>Result of a future write, with the values read back from the EC.</summary>
public sealed record OemApplyResult(bool Ok, OemPowerState? ReadBack, string? ErrorKey);
