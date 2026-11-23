namespace UnlockGpu.Core.Services;

/// <summary>
/// Optional capability of an <see cref="Nvml.IGpu"/>: a counter that increases every time the GPU library had to be
/// (re)initialised. <see cref="GpuMonitor"/> raises <see cref="IGpuMonitor.DriverReset"/> when it changes.
/// Implementations that do not expose it are still covered by the other reset heuristics.
/// </summary>
public interface IGpuInitGeneration
{
    /// <summary>Number of successful initialisations so far.</summary>
    int InitGeneration { get; }
}
