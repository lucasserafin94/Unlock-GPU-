using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>What the status bar says about the hardware versus the active profile.</summary>
public enum DriftState
{
    /// <summary>No profile applied yet: the GPU runs on the driver defaults (or values set elsewhere).</summary>
    NoProfile,
    /// <summary>Everything the active profile sets matches the hardware.</summary>
    Match,
    /// <summary>At least one value differs from the active profile.</summary>
    Drift,
    /// <summary>The hardware was not read yet (GPU unavailable or first sample pending).</summary>
    Unknown,
}

/// <summary>
/// Status bar: compares what the GPU reports now (offsets read by the monitor on every sample, VRAM lock detection)
/// with the active profile (last applied, or the startup profile) and says what differs, with "Apply" and "Reset"
/// buttons. Purely a reader: nothing is written until the user presses a button.
/// </summary>
public sealed class DriftViewModel : ObservableObject
{
    readonly Localizer _l;
    readonly IProfileStore _store;
    readonly ISettingsStore _settings;
    readonly UiStateStore _uiState;
    readonly Func<string, Task> _apply;
    readonly Func<Task> _reset;
    string? _profileName;
    Profile? _profile;
    DriftState _state = DriftState.Unknown;
    string _text = "";
    string _detail = "";
    bool _working;
    GpuSample? _lastSample;
    VramLockState? _lastLock;

    /// <summary>Creates the status bar model.</summary>
    /// <param name="localizer">Texts.</param>
    /// <param name="store">Profiles.</param>
    /// <param name="settings">Settings (startup profile).</param>
    /// <param name="uiState">Last applied profile.</param>
    /// <param name="apply">Applies a profile by name (reports its own result in the main banner).</param>
    /// <param name="reset">Returns offsets and VRAM lock to the driver defaults.</param>
    public DriftViewModel(Localizer localizer, IProfileStore store, ISettingsStore settings, UiStateStore uiState,
        Func<string, Task> apply, Func<Task> reset)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(uiState);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(reset);
        _l = localizer;
        _store = store;
        _settings = settings;
        _uiState = uiState;
        _apply = apply;
        _reset = reset;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => _profileName != null && !_working, null);
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => !_working, null);
        ReloadProfile();
    }

    /// <summary>Current comparison result.</summary>
    public DriftState State { get => _state; private set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(IsDrift)); } }
    /// <summary>True when something differs (the bar turns amber).</summary>
    public bool IsDrift => _state == DriftState.Drift;
    /// <summary>One-line summary.</summary>
    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    /// <summary>Full list of differences (tooltip).</summary>
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    /// <summary>Re-applies the active profile.</summary>
    public AsyncRelayCommand ApplyCommand { get; }
    /// <summary>Back to the driver defaults.</summary>
    public AsyncRelayCommand ResetCommand { get; }

    /// <summary>Re-reads which profile is active and its contents (after apply, save, delete, settings change).</summary>
    public void ReloadProfile()
    {
        var st = _settings.Current;
        string? name = _uiState.Current.LastProfile ?? (st.ApplyProfileAtStartup ? st.StartupProfile : null);
        _profileName = name;
        _profile = name == null ? null : _store.Get(name);
        if (_profile == null) _profileName = null;
        ApplyCommand.RaiseCanExecuteChanged();
        Recompute();
    }

    /// <summary>New sample (UI thread). Cheap: compares numbers already read by the monitor.</summary>
    public void OnSample(SampleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _lastSample = context.Sample;
        _lastLock = context.Lock;
        Recompute();
    }

    /// <summary>Language changed.</summary>
    public void OnLanguageChanged() => Recompute();

    void Recompute()
    {
        var s = _lastSample;
        if (_profile == null || _profileName == null)
        {
            State = DriftState.NoProfile;
            Text = _l.T("ui.drift.noProfile");
            Detail = _l.T("ui.drift.noProfileHint");
            return;
        }
        if (s is not { Ok: true } || (s.CoreOffsetMHz == null && s.MemOffsetMHz == null))
        {
            State = DriftState.Unknown;
            Text = _l.T("ui.drift.unknown", ("name", _profileName));
            Detail = "";
            return;
        }

        var c = _l.Culture;
        var diffs = new List<string>();
        if (_profile.CoreOffsetMHz is int pc && s.CoreOffsetMHz is int nc && pc != nc)
            diffs.Add(_l.T("ui.drift.core", ("now", Format.Offset(nc, c)), ("want", Format.Offset(pc, c))));
        if (_profile.MemOffsetMHz is int pm && s.MemOffsetMHz is int nm && pm != nm)
            diffs.Add(_l.T("ui.drift.mem", ("now", Format.Offset(nm, c)), ("want", Format.Offset(pm, c))));
        if (_profile.VramLockMHz is uint lv && _lastLock is { } lk)
        {
            bool wantLocked = lv != 0;
            bool isLocked = lk.Locked && !lk.Lost;
            if (wantLocked && (!isLocked || lk.LevelMHz != lv))
                diffs.Add(_l.T("ui.drift.vramWantLocked", ("want", Format.Number(lv, c))));
            else if (!wantLocked && isLocked)
                diffs.Add(_l.T("ui.drift.vramWantAuto"));
        }

        if (diffs.Count == 0)
        {
            State = DriftState.Match;
            Text = _l.T("ui.drift.match", ("name", _profileName));
            Detail = "";
        }
        else
        {
            State = DriftState.Drift;
            Text = _l.T("ui.drift.differs", ("name", _profileName)) + " " + string.Join("; ", diffs);
            Detail = string.Join(Environment.NewLine, diffs.Select(d => "• " + d));
        }
    }

    async Task ApplyAsync()
    {
        if (_profileName == null) return;
        await RunAsync(() => _apply(_profileName)).ConfigureAwait(true);
    }

    Task ResetAsync() => RunAsync(_reset);

    async Task RunAsync(Func<Task> action)
    {
        _working = true;
        ApplyCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
        try { await action().ConfigureAwait(true); }
        finally
        {
            _working = false;
            ReloadProfile();
            ResetCommand.RaiseCanExecuteChanged();
        }
    }
}
