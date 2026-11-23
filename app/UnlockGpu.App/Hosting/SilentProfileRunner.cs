using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.Hosting;

/// <summary>
/// <c>--silent --apply-profile "&lt;name&gt;"</c>: applies one profile without any UI (used by the logon task) and
/// returns the process exit code: 0 when every item was applied and read back, 1 otherwise. Results go to the log.
/// </summary>
public sealed class SilentProfileRunner
{
    readonly AppPaths _paths;
    readonly ILog _log;
    readonly TimeSpan _gpuWait;

    /// <summary>Creates the runner.</summary>
    /// <param name="paths">App folders.</param>
    /// <param name="log">Log.</param>
    /// <param name="gpuWait">How long to wait for the driver at logon (default 60 s).</param>
    public SilentProfileRunner(AppPaths paths, ILog log, TimeSpan? gpuWait = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _paths = paths;
        _log = log;
        _gpuWait = gpuWait ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Applies <paramref name="profileName"/>; returns the exit code.</summary>
    public async Task<int> RunAsync(string? profileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            _log.Warn("SILENT: no --apply-profile given; nothing to do");
            return 1;
        }
        try
        {
            var profile = new ProfileStore(_paths).Get(profileName);
            if (profile == null)
            {
                _log.Warn($"SILENT: profile '{profileName}' not found");
                return 1;
            }

            using var gpu = new NvmlGpu();
            var deadline = DateTime.UtcNow + _gpuWait;
            while (!gpu.IsAvailable)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    _log.Warn("SILENT: GPU not available: " + gpu.LastError);
                    return 1;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }

            var tracker = new OffsetTracker();
            var offsets = new OffsetService(gpu, _log, tracker);
            var vram = new VramLockService(gpu, _paths, _log);
            var power = new PowerService(gpu, _paths, _log);
            var applier = new ProfileApplier(gpu, offsets, vram, power, _log);
            var results = applier.Apply(profile);
            foreach (var r in results)
                _log.Info($"SILENT: {(r.Ok ? "ok" : "FAILED")} requested={r.RequestedText} readBack={r.ReadBackText} error={r.ErrorText}");
            bool ok = results.All(r => r.Ok);
            _log.Info($"SILENT: profile '{profile.Name}' {(ok ? "applied" : "had failures")}");
            return ok ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            _log.Warn("SILENT: cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            _log.Error("SILENT: failed", ex);
            return 1;
        }
    }
}
