using UnlockGpu.App.Mvvm;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>One monitor reading plus values derived once for all tabs (computed off the UI thread).</summary>
/// <param name="Sample">The reading.</param>
/// <param name="Lock">VRAM lock evaluation for this reading (null when it could not be evaluated).</param>
/// <param name="AveragePower30W">Average power of the last 30 s (NaN when unknown).</param>
/// <param name="History">Monitor history (oldest first) for the graph.</param>
public sealed record SampleContext(GpuSample Sample, VramLockState? Lock, double AveragePower30W, IReadOnlyList<GpuSample> History);

/// <summary>
/// Base for the tab view-models. The main view-model calls these hooks on the UI thread, so tabs never subscribe to
/// background events themselves.
/// </summary>
public abstract class TabViewModel : ObservableObject
{
    /// <summary>Creates the tab over the shared localizer.</summary>
    protected TabViewModel(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        L = localizer;
    }

    /// <summary>The localizer.</summary>
    protected Localizer L { get; }

    /// <summary>The language changed: re-format texts built in code.</summary>
    public virtual void OnLanguageChanged() { }

    /// <summary>A new monitor reading arrived.</summary>
    public virtual void OnSample(SampleContext context) { }

    /// <summary>The settings were saved (by any tab or by a backup import).</summary>
    public virtual void OnSettingsChanged(AppSettings settings) { }
}
