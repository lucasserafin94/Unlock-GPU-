namespace UnlockGpu.Core.Model;

/// <summary>Uma leitura da GPU (tudo via NVML publica).</summary>
public sealed record GpuSample
{
    public required DateTime Time { get; init; }
    public string PState { get; init; } = "?";
    public uint CoreMHz { get; init; }
    public uint SmMHz { get; init; }
    public uint MemMHz { get; init; }
    public double PowerW { get; init; } = double.NaN;
    public double LimitW { get; init; } = double.NaN;
    public uint TempC { get; init; }
    public uint UtilGpu { get; init; }
    public uint UtilMem { get; init; }
    public double VramUsedMB { get; init; }
    public double VramTotalMB { get; init; }
    public ClockReasons Reasons { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
}

/// <summary>Dados fixos da GPU, lidos uma vez.</summary>
public sealed record GpuInfo(string Name, string Driver, string Vbios, double MinLimitW, double DefaultLimitW, double MaxLimitW,
    uint MaxCoreMHz, IReadOnlyList<uint> MemClocks);

/// <summary>Offset de clock de um dominio, com a faixa que o driver aceita.</summary>
public sealed record OffsetInfo(int CurrentMHz, int MinMHz, int MaxMHz);

public enum ClockDomain { Core = 0, Memory = 2 }

/// <summary>nvmlClocksEventReasons (nvml.h).</summary>
[Flags]
public enum ClockReasons : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationsClocksSetting = 0x2,
    SwPowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    SwThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrakeSlowdown = 0x80,
    DisplayClockSetting = 0x100,
    BoardLimit = 0x200,
    Reliability = 0x400,
}

public enum ReasonSeverity { Ok, Info, Warning }

/// <summary>Uma etiqueta de corte: chave de traducao + gravidade.</summary>
public sealed record ReasonTag(string Key, ReasonSeverity Severity, ulong Bits);

public static class ClockReasonText
{
    static readonly (ClockReasons Flag, string Key, ReasonSeverity Sev)[] Map =
    [
        (ClockReasons.SwPowerCap, "reason.swPowerCap", ReasonSeverity.Warning),
        (ClockReasons.HwPowerBrakeSlowdown, "reason.hwPowerBrake", ReasonSeverity.Warning),
        (ClockReasons.HwThermalSlowdown, "reason.hwThermal", ReasonSeverity.Warning),
        (ClockReasons.SwThermalSlowdown, "reason.swThermal", ReasonSeverity.Warning),
        (ClockReasons.HwSlowdown, "reason.hwSlowdown", ReasonSeverity.Warning),
        (ClockReasons.BoardLimit, "reason.boardLimit", ReasonSeverity.Warning),
        (ClockReasons.ApplicationsClocksSetting, "reason.appClocks", ReasonSeverity.Info),
        (ClockReasons.SyncBoost, "reason.syncBoost", ReasonSeverity.Info),
        (ClockReasons.DisplayClockSetting, "reason.displayClock", ReasonSeverity.Info),
        (ClockReasons.Reliability, "reason.reliability", ReasonSeverity.Info),
        (ClockReasons.GpuIdle, "reason.idle", ReasonSeverity.Info),
    ];

    const ulong Known = 0x7FF;

    public static IReadOnlyList<ReasonTag> Tags(ClockReasons r)
    {
        var list = new List<ReasonTag>();
        foreach (var (flag, key, sev) in Map)
            if (r.HasFlag(flag)) list.Add(new ReasonTag(key, sev, (ulong)flag));
        ulong unknown = (ulong)r & ~Known;
        if (unknown != 0) list.Add(new ReasonTag("reason.other", ReasonSeverity.Info, unknown));
        if (list.Count == 0) list.Add(new ReasonTag("reason.none", ReasonSeverity.Ok, 0));
        return list;
    }

    public static bool CostsPerformance(ClockReasons r) =>
        (r & (ClockReasons.SwPowerCap | ClockReasons.HwPowerBrakeSlowdown | ClockReasons.HwThermalSlowdown |
              ClockReasons.SwThermalSlowdown | ClockReasons.HwSlowdown | ClockReasons.BoardLimit)) != 0;
}

/// <summary>Resultado de aplicar algo + leitura de volta do hardware.</summary>
public sealed record ApplyResult(bool Ok, string RequestedText, string ReadBackText, string? ErrorText = null);
