using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>A profile row.</summary>
/// <param name="Name">Profile name.</param>
/// <param name="Details">What the profile changes, formatted.</param>
/// <param name="Saved">When it was saved.</param>
public sealed record ProfileItem(string Name, string Details, string Saved);

/// <summary>
/// Profiles tab: list, save current, apply, delete, backup export/import (start with Windows lives in the Options tab).
/// Also the single place that applies a profile by name (tray menu and driver-reset reapply use it).
/// </summary>
public sealed class ProfilesViewModel : TabViewModel
{
    readonly IProfileStore _store;
    readonly IProfileApplier _applier;
    readonly IBackupService _backup;
    readonly ISettingsStore _settings;
    readonly IDialogService _dialogs;
    readonly UiStateStore _uiState;
    readonly BannerViewModel _banner;
    ProfileItem? _selected;
    string _newName = "";

    /// <summary>Creates the tab.</summary>
    public ProfilesViewModel(Localizer localizer, IProfileStore store, IProfileApplier applier,
        IBackupService backup, ISettingsStore settings, IDialogService dialogs, UiStateStore uiState, BannerViewModel banner)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(uiState);
        ArgumentNullException.ThrowIfNull(banner);
        _store = store;
        _applier = applier;
        _backup = backup;
        _settings = settings;
        _dialogs = dialogs;
        _uiState = uiState;
        _banner = banner;
        void OnError(Exception ex) => _banner.Show(ResultText.Exception(L, ex), BannerKind.Error);

        SaveCurrentCommand = new AsyncRelayCommand(SaveCurrentAsync, () => !string.IsNullOrWhiteSpace(_newName), OnError);
        ApplyCommand = new AsyncRelayCommand(() => ApplyByNameAsync(_selected?.Name), () => _selected != null, OnError);
        DeleteCommand = new RelayCommand(Delete, () => _selected != null);
        ResetDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync, null, OnError);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync, null, OnError);
        ImportBackupCommand = new AsyncRelayCommand(ImportBackupAsync, null, OnError);
        Refresh();
    }

    /// <summary>Raised after something was applied to the GPU (so other tabs re-read the hardware).</summary>
    public event EventHandler? Applied;

    /// <summary>Profiles.</summary>
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    /// <summary>True when at least one profile exists.</summary>
    public bool HasProfiles => Profiles.Count > 0;
    /// <summary>Raised after the profile list changed (saved, deleted, imported).</summary>
    public event EventHandler? ProfilesChanged;

    /// <summary>Selected profile.</summary>
    public ProfileItem? SelectedProfile
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            if (value != null) NewName = value.Name;
            ApplyCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Name for "save current".</summary>
    public string NewName { get => _newName; set { if (SetProperty(ref _newName, value)) SaveCurrentCommand.RaiseCanExecuteChanged(); } }

    /// <summary>Captures the hardware state and saves it under <see cref="NewName"/>.</summary>
    public AsyncRelayCommand SaveCurrentCommand { get; }
    /// <summary>Applies the selected profile.</summary>
    public AsyncRelayCommand ApplyCommand { get; }
    /// <summary>Deletes the selected profile (asks first).</summary>
    public RelayCommand DeleteCommand { get; }
    /// <summary>Offsets 0 and VRAM unlocked.</summary>
    public AsyncRelayCommand ResetDefaultsCommand { get; }
    /// <summary>Exports a backup ZIP.</summary>
    public AsyncRelayCommand ExportBackupCommand { get; }
    /// <summary>Imports a backup ZIP (nothing is applied to the GPU).</summary>
    public AsyncRelayCommand ImportBackupCommand { get; }

    /// <summary>Applies the profile called <paramref name="name"/> in the background and reports the results.</summary>
    /// <returns>The results, or null when the profile does not exist.</returns>
    public async Task<IReadOnlyList<ApplyResult>?> ApplyByNameAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var profile = _store.Get(name);
        if (profile == null)
        {
            _banner.Show(L.T("ui.profiles.notFound", ("name", name)), BannerKind.Warning);
            return null;
        }
        var results = await Task.Run(() => _applier.Apply(profile)).ConfigureAwait(true);
        _uiState.SetLastProfile(profile.Name);
        bool ok = results.All(r => r.Ok);
        _banner.Show(L.T("ui.profiles.applied", ("name", profile.Name)) + Environment.NewLine + ResultText.Describe(L, results),
            ok ? BannerKind.Success : BannerKind.Error);
        Applied?.Invoke(this, EventArgs.Empty);
        return results;
    }

    /// <summary>Returns offsets and VRAM lock to the driver defaults.</summary>
    public async Task ResetToDefaultsAsync()
    {
        var results = await Task.Run(_applier.ResetToDefaults).ConfigureAwait(true);
        _uiState.SetLastProfile(null);
        _banner.Show(L.T("ui.profiles.resetDone") + Environment.NewLine + ResultText.Describe(L, results),
            results.All(r => r.Ok) ? BannerKind.Success : BannerKind.Error);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Names of the saved profiles (tray menu).</summary>
    public IReadOnlyList<string> ListNames() => _store.List().Select(p => p.Name).ToArray();

    /// <inheritdoc/>
    public override void OnLanguageChanged() => Refresh();

    async Task SaveCurrentAsync()
    {
        string name = _newName.Trim();
        if (_store.Get(name) != null && !_dialogs.Confirm(L.T("app.name"), L.T("ui.profiles.overwrite", ("name", name)))) return;
        var profile = await Task.Run(() => _applier.Capture(name)).ConfigureAwait(true);
        _store.Save(profile);
        Refresh();
        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        _banner.Show(L.T("ui.profiles.saved", ("name", profile.Name)), BannerKind.Success);
    }

    void Delete()
    {
        if (_selected == null) return;
        string name = _selected.Name;
        if (!_dialogs.Confirm(L.T("app.name"), L.T("ui.profiles.confirmDelete", ("name", name)))) return;
        _store.Delete(name);
        Refresh();
    }

    async Task ExportBackupAsync()
    {
        string name = "UnlockGPU-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip";
        string? path = _dialogs.SaveFile(L.T("ui.backup.exportTitle"), L.T("ui.dialog.zipFilter"), name);
        if (path == null) return;
        string written = await Task.Run(() => _backup.Export(path)).ConfigureAwait(true);
        _banner.Show(L.T("ui.backup.exported", ("file", Path.GetFileName(written))), BannerKind.Success);
    }

    async Task ImportBackupAsync()
    {
        string? path = _dialogs.OpenFile(L.T("ui.backup.importTitle"), L.T("ui.dialog.zipFilter"));
        if (path == null) return;
        if (!_dialogs.Confirm(L.T("app.name"), L.T("ui.backup.confirmImport"))) return;
        await Task.Run(() => _backup.Import(path)).ConfigureAwait(true);
        Refresh();
        _banner.Show(L.T("ui.backup.imported"), BannerKind.Success);
    }

    void Refresh()
    {
        string? keep = _selected?.Name;
        var list = _store.List();
        Profiles.Clear();
        foreach (var p in list) Profiles.Add(new ProfileItem(p.Name, Describe(p), p.Saved.ToString("g", L.Culture)));
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        _selected = Profiles.FirstOrDefault(p => p.Name == keep);
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(SelectedProfile));
        ApplyCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    string Describe(Profile p)
    {
        var c = L.Culture;
        var parts = new List<string>();
        if (p.CoreOffsetMHz is int core) parts.Add(L.T("ui.profiles.d.core", ("v", Format.Offset(core, c))));
        if (p.MemOffsetMHz is int mem) parts.Add(L.T("ui.profiles.d.mem", ("v", Format.Offset(mem, c))));
        if (p.VramLockMHz is uint lv) parts.Add(lv == 0 ? L.T("ui.profiles.d.vramAuto") : L.T("ui.profiles.d.vram", ("v", Format.Number(lv, c))));
        if (p.PowerLimitW is double w) parts.Add(L.T("ui.profiles.d.power", ("v", Format.WattsWhole(w, c))));
        return parts.Count == 0 ? L.T("ui.profiles.d.empty") : string.Join(" · ", parts);
    }
}
