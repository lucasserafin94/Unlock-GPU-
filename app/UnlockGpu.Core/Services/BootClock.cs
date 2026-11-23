namespace UnlockGpu.Core.Services;

/// <summary>Identifies the current Windows boot without WMI: boot time = now - system uptime.</summary>
public static class BootClock
{
    /// <summary>Two boot times closer than this are the same boot (uptime and wall clock drift slightly).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>Approximate UTC time of the current boot.</summary>
    public static DateTime BootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>True when two boot times belong to the same boot.</summary>
    public static bool SameBoot(DateTime a, DateTime b) => (a - b).Duration() <= Tolerance;
}
