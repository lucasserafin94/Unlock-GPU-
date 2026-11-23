using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.App.Services;

/// <summary>Small UI state persisted next to the settings (<see cref="AppPaths.StateFile"/>).</summary>
public sealed record UiState
{
    /// <summary>Last profile the user applied (reapplied after a driver reset when no startup profile is set).</summary>
    public string? LastProfile { get; init; }
}

/// <summary>Loads and saves <see cref="UiState"/> (atomic JSON).</summary>
public sealed class UiStateStore
{
    readonly string _file;
    readonly Lock _gate = new();
    UiState _current;

    /// <summary>Loads the state file (defaults when missing).</summary>
    public UiStateStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = paths.StateFile;
        _current = JsonStore.Load<UiState>(_file) ?? new UiState();
    }

    /// <summary>Current state.</summary>
    public UiState Current { get { lock (_gate) return _current; } }

    /// <summary>Remembers the last applied profile.</summary>
    public void SetLastProfile(string? name)
    {
        lock (_gate)
        {
            _current = _current with { LastProfile = name };
            try { JsonStore.Save(_file, _current); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
