namespace UnlockGpu.Core.Services;

/// <summary>
/// Localizer keys produced by the core services (error texts, health reasons, notes the UI must show).
/// Texts live in <c>i18n/parts/core-services.*.json</c>.
/// </summary>
public static class ServiceTextKeys
{
    // ---- generic ----
    /// <summary>The GPU (NVML) is not available.</summary>
    public const string GpuUnavailable = "svc.error.gpuUnavailable";

    // ---- offsets ----
    /// <summary>The driver did not report the offset range for a domain.</summary>
    public const string OffsetUnavailable = "svc.offset.error.unavailable";
    /// <summary>A write was refused because the auto-tuner holds the GPU.</summary>
    public const string TunerBusy = "svc.tunerBusy";
    /// <summary>Requested offset outside the range reported by the driver; nothing was applied.</summary>
    public const string OffsetOutOfRange = "svc.offset.error.outOfRange";
    /// <summary>The driver refused the offset.</summary>
    public const string OffsetDriver = "svc.offset.error.driver";
    /// <summary>The value read back differs from the requested value.</summary>
    public const string OffsetReadBack = "svc.offset.error.readBack";

    // ---- VRAM lock ----
    /// <summary>No lockable memory clock levels were reported.</summary>
    public const string VramNoLevels = "svc.vram.error.noLevels";
    /// <summary>The requested level is not one of the lockable levels.</summary>
    public const string VramInvalidLevel = "svc.vram.error.invalidLevel";
    /// <summary>The driver refused to lock/unlock the memory clock.</summary>
    public const string VramDriver = "svc.vram.error.driver";
    /// <summary>Mandatory note: locking memory lowers total power when the GPU is power-capped.</summary>
    public const string VramPowerCapNote = "svc.vram.note.powerCapped";

    // ---- power limit ----
    /// <summary>This GPU/driver does not allow changing the power limit.</summary>
    public const string PowerNotSupported = "svc.power.error.notSupported";
    /// <summary>Requested limit outside the driver constraints.</summary>
    public const string PowerOutOfRange = "svc.power.error.outOfRange";
    /// <summary>The driver refused the power limit.</summary>
    public const string PowerDriver = "svc.power.error.driver";
    /// <summary>The enforced limit read back differs from the requested value.</summary>
    public const string PowerReadBack = "svc.power.error.readBack";

    // ---- stability ----
    /// <summary>Performance-costing clock reasons under load for several consecutive samples.</summary>
    public const string HealthThrottling = "svc.health.throttling";
    /// <summary>Temperature at or above the warning threshold.</summary>
    public const string HealthHot = "svc.health.hot";
    /// <summary>Temperature at or above the critical threshold.</summary>
    public const string HealthOverheat = "svc.health.overheat";
    /// <summary>Benchmark score dropped beyond the threshold.</summary>
    public const string HealthBenchmarkDrop = "svc.health.benchmarkDrop";
    /// <summary>Kernel-Power 41 or WHEA-Logger events since the app started.</summary>
    public const string HealthSystemEvents = "svc.health.systemEvents";

    // ---- startup ----
    /// <summary>Profile name unusable in a command line.</summary>
    public const string StartupInvalidName = "svc.startup.error.invalidName";
    /// <summary>The executable path could not be determined.</summary>
    public const string StartupNoExe = "svc.startup.error.noExe";
    /// <summary>schtasks failed to create the task.</summary>
    public const string StartupCreateFailed = "svc.startup.error.create";
    /// <summary>schtasks failed to delete the task.</summary>
    public const string StartupDeleteFailed = "svc.startup.error.delete";

    // ---- profiles / backup ----
    /// <summary>Profile name is empty or invalid.</summary>
    public const string ProfileInvalidName = "svc.profile.error.invalidName";
    /// <summary>The file is not a valid Unlock GPU backup.</summary>
    public const string BackupInvalid = "svc.backup.error.invalid";

    // ---- rom override (persistent power ceiling, driver class key) ----
    /// <summary>No NVIDIA display device was found via WMI.</summary>
    public const string RomNoGpu = "svc.rom.error.noGpu";
    /// <summary>The GPU driver class key could not be opened (usually: not running as administrator).</summary>
    public const string RomKeyOpen = "svc.rom.error.keyOpen";
    /// <summary>The capture file has no valid romOverride entries.</summary>
    public const string RomCaptureInvalid = "svc.rom.error.captureInvalid";
    /// <summary>A staged slot did not read back identical to what was written.</summary>
    public const string RomReadBackMismatch = "svc.rom.error.readBack";
    /// <summary>The device restart (and the hard-cycle fallback) failed.</summary>
    public const string RomRestartFailed = "svc.rom.error.restartFailed";
    /// <summary>The live power ceiling after the restart differs from the staged value.</summary>
    public const string RomMaxMismatch = "svc.rom.error.maxMismatch";
    /// <summary>The slot does not exist in the driver key.</summary>
    public const string RomSlotMissing = "svc.rom.error.slotMissing";
    /// <summary>The slot blob has a layout that cannot be value-edited (payload width other than 4 or 8).</summary>
    public const string RomLayoutUnsupported = "svc.rom.error.layoutUnsupported";
    /// <summary>The new raw value does not fit an int32.</summary>
    public const string RomValueRange = "svc.rom.error.valueRange";
}
