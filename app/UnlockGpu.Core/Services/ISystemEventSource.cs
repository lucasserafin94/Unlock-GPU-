namespace UnlockGpu.Core.Services;

/// <summary>One relevant Windows System log event (Kernel-Power 41 or WHEA-Logger).</summary>
/// <param name="TimeUtc">When it was logged.</param>
/// <param name="Provider">Provider name, e.g. Microsoft-Windows-Kernel-Power.</param>
/// <param name="EventId">Event id.</param>
public sealed record SystemEventRecord(DateTime TimeUtc, string Provider, int EventId);

/// <summary>Read-only access to stability-relevant events in the Windows System log.</summary>
public interface ISystemEventSource
{
    /// <summary>Kernel-Power 41 and WHEA-Logger events logged at or after <paramref name="sinceUtc"/>, newest first.</summary>
    IReadOnlyList<SystemEventRecord> Query(DateTime sinceUtc);
}
