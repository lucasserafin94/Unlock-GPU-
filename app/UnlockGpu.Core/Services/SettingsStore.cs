using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Services;

/// <summary>App settings persisted as JSON in <see cref="AppPaths.SettingsFile"/> (atomic writes).</summary>
public sealed class SettingsStore : ISettingsStore
{
    readonly AppPaths _paths;
    readonly Lock _gate = new();
    AppSettings _current;

    /// <summary>Loads the settings file, falling back to defaults when it is missing or corrupt.</summary>
    public SettingsStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _current = JsonStore.Load<AppSettings>(paths.SettingsFile) ?? new AppSettings();
    }

    /// <inheritdoc/>
    public AppSettings Current { get { lock (_gate) return _current; } }

    /// <inheritdoc/>
    public event EventHandler<AppSettings>? Changed;

    /// <inheritdoc/>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            JsonStore.Save(_paths.SettingsFile, settings);
            _current = settings;
        }
        Changed?.Invoke(this, settings);
    }
}
