using UnlockGpu.App.Services;
using UnlockGpu.Core.Bench;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Oem;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.Hosting;

/// <summary>
/// Composition of the core services for the interactive app. Created once by <see cref="App"/>; owns and disposes
/// everything it creates. One <see cref="OffsetTracker"/> is shared by the offset service and the monitor so that an
/// offset the user sets to 0 is not reported as a driver reset.
/// </summary>
public sealed class AppServices : IDisposable
{
    readonly NvmlGpu _gpu;
    readonly GpuMonitor _monitor;
    readonly SessionRecorder _recorder;
    readonly UniwillOemPowerService _oem;
    bool _disposed;

    /// <summary>Creates all services. Nothing is applied to the GPU here.</summary>
    public AppServices(AppPaths paths, ILog log, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        Paths = paths;
        Log = log;
        Settings = settings;

        _gpu = new NvmlGpu();
        Tracker = new OffsetTracker();
        WriteGate = new WriteGate();
        Offsets = new OffsetService(_gpu, log, Tracker, WriteGate);
        Vram = new VramLockService(_gpu, paths, log, gate: WriteGate);
        Power = new PowerService(_gpu, paths, log, WriteGate);
        Applier = new ProfileApplier(_gpu, Offsets, Vram, Power, log);
        Profiles = new ProfileStore(paths);
        _monitor = new GpuMonitor(_gpu, log, Tracker);
        _recorder = new SessionRecorder(paths, log);
        Stability = new StabilityMonitor(log, new WindowsSystemEventSource());
        Startup = new StartupService(log);
        Backup = new BackupService(paths, settings, Profiles, Applier);
        Tuner = new AutoTuner(_gpu, Offsets, new D3D11Benchmark(log), new D3D11VramTester(log), log, WriteGate);
        _oem = UniwillOemPowerService.CreateDefault(log);
        UiState = new UiStateStore(paths);
    }

    /// <summary>App folders.</summary>
    public AppPaths Paths { get; }
    /// <summary>File log.</summary>
    public ILog Log { get; }
    /// <summary>Settings store.</summary>
    public ISettingsStore Settings { get; }
    /// <summary>The GPU (NVML).</summary>
    public IGpu Gpu => _gpu;
    /// <summary>Shared offset tracker.</summary>
    public OffsetTracker Tracker { get; }
    /// <summary>Exclusive GPU writes while the auto-tuner runs.</summary>
    public WriteGate WriteGate { get; }
    /// <summary>Clock offsets.</summary>
    public IOffsetService Offsets { get; }
    /// <summary>VRAM clock lock.</summary>
    public IVramLockService Vram { get; }
    /// <summary>Power limit.</summary>
    public IPowerService Power { get; }
    /// <summary>Profile applier.</summary>
    public IProfileApplier Applier { get; }
    /// <summary>Profile store.</summary>
    public IProfileStore Profiles { get; }
    /// <summary>Background monitor.</summary>
    public IGpuMonitor Monitor => _monitor;
    /// <summary>Session recorder.</summary>
    public ISessionRecorder Recorder => _recorder;
    /// <summary>Health light.</summary>
    public IStabilityMonitor Stability { get; }
    /// <summary>Start with Windows.</summary>
    public IStartupService Startup { get; }
    /// <summary>Backup ZIP.</summary>
    public IBackupService Backup { get; }
    /// <summary>Automatic tuner.</summary>
    public IAutoTuner Tuner { get; }
    /// <summary>OEM firmware (read-only).</summary>
    public IOemPowerService Oem => _oem;
    /// <summary>Small UI state (last profile).</summary>
    public UiStateStore UiState { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.Dispose();
        _recorder.Dispose();
        _oem.Dispose();
        _gpu.Dispose();
    }
}
