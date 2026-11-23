using System.Collections.ObjectModel;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>
/// "Start with Windows" card (Options tab): start at logon (elevated through Task Scheduler, no UAC prompt), start
/// hidden in the tray and keep running, and optionally apply a profile automatically. Every change re-registers the
/// scheduled task with the current combination; the real task state is read back at startup.
/// </summary>
public sealed class StartupViewModel : ObservableObject
{
    readonly Localizer _l;
    readonly IStartupService _startup;
    readonly IProfileStore _profiles;
    readonly ISettingsStore _settings;
    readonly BannerViewModel _banner;
    bool _enabled;
    bool _busy;
    bool _syncing;

    /// <summary>Creates the card.</summary>
    public StartupViewModel(Localizer localizer, IStartupService startup, IProfileStore profiles, ISettingsStore settings, BannerViewModel banner)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(banner);
        _l = localizer;
        _startup = startup;
        _profiles = profiles;
        _settings = settings;
        _banner = banner;
        _enabled = settings.Current.StartWithWindows;
        RefreshProfiles();
    }

    /// <summary>Saved profile names.</summary>
    public ObservableCollection<string> ProfileNames { get; } = [];
    /// <summary>True when at least one profile exists.</summary>
    public bool HasProfiles => ProfileNames.Count > 0;

    /// <summary>Start with Windows.</summary>
    public bool StartWithWindows
    {
        get => _enabled;
        set
        {
            if (_busy || _syncing || !SetProperty(ref _enabled, value)) return;
            _ = SyncTaskAsync(value);
        }
    }

    /// <summary>Start hidden in the tray and keep running.</summary>
    public bool StartInTray
    {
        get => _settings.Current.StartInTray;
        set
        {
            if (value == _settings.Current.StartInTray) return;
            _settings.Save(_settings.Current with { StartInTray = value });
            OnPropertyChanged();
            if (_enabled) _ = SyncTaskAsync(true);
        }
    }

    /// <summary>Apply <see cref="StartupProfile"/> automatically when the app starts with Windows.</summary>
    public bool ApplyProfileAtStartup
    {
        get => _settings.Current.ApplyProfileAtStartup && _settings.Current.StartupProfile != null;
        set
        {
            if (_syncing) return;
            if (value && _settings.Current.StartupProfile == null)
            {
                string? first = ProfileNames.FirstOrDefault();
                if (first == null)
                {
                    _banner.Show(_l.T("ui.startup.noProfiles"), BannerKind.Warning);
                    OnPropertyChanged();
                    return;
                }
                _settings.Save(_settings.Current with { StartupProfile = first, ApplyProfileAtStartup = true });
                OnPropertyChanged(nameof(StartupProfile));
            }
            else if (value == _settings.Current.ApplyProfileAtStartup) { OnPropertyChanged(); return; }
            else _settings.Save(_settings.Current with { ApplyProfileAtStartup = value });
            OnPropertyChanged();
            if (_enabled) _ = SyncTaskAsync(true);
        }
    }

    /// <summary>Profile applied at startup.</summary>
    public string? StartupProfile
    {
        get => _settings.Current.StartupProfile;
        set
        {
            // While the list is rebuilt the combo briefly reports null; keep the stored choice.
            if (_syncing || value == null || value == _settings.Current.StartupProfile) return;
            _settings.Save(_settings.Current with { StartupProfile = value });
            OnPropertyChanged();
            if (_enabled && ApplyProfileAtStartup) _ = SyncTaskAsync(true);
        }
    }

    /// <summary>True while the scheduled task is being changed.</summary>
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(NotBusy)); } }
    /// <summary>Inverse of <see cref="Busy"/> (enables the switches).</summary>
    public bool NotBusy => !_busy;

    /// <summary>Reads the real state of the scheduled task (background; schtasks query only).</summary>
    public async Task InitializeAsync()
    {
        try
        {
            bool enabled = await Task.Run(() => _startup.IsEnabled).ConfigureAwait(true);
            SetEnabledSilently(enabled);
            if (_settings.Current.StartWithWindows != enabled)
                _settings.Save(_settings.Current with { StartWithWindows = enabled });
        }
        catch (Exception ex)
        {
            _banner.Show(ResultText.Exception(_l, ex), BannerKind.Error);
        }
    }

    /// <summary>Rebuilds the profile list; a startup profile that no longer exists is dropped (and the task updated).</summary>
    public void RefreshProfiles()
    {
        var names = _profiles.List().Select(p => p.Name).ToArray();
        _syncing = true;
        ProfileNames.Clear();
        foreach (string n in names) ProfileNames.Add(n);
        _syncing = false;
        string? current = _settings.Current.StartupProfile;
        if (current != null && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Save(_settings.Current with { StartupProfile = null });
            if (_enabled) _ = SyncTaskAsync(true);
        }
        OnPropertiesChanged(nameof(HasProfiles), nameof(StartupProfile), nameof(ApplyProfileAtStartup));
    }

    /// <summary>Settings were saved elsewhere (e.g. backup import): refresh the bindings.</summary>
    public void OnSettingsChanged() =>
        OnPropertiesChanged(nameof(StartInTray), nameof(ApplyProfileAtStartup), nameof(StartupProfile));

    // Mudancas seguidas (trocar perfil, bandeja, liga/desliga) viram uma fila: cada registro da tarefa roda depois do
    // anterior e le as configuracoes no momento em que roda, entao vale sempre a ultima escolha.
    Task _chain = Task.CompletedTask;

    Task SyncTaskAsync(bool enable)
    {
        _chain = RunAfter(_chain, enable);
        return _chain;
    }

    async Task RunAfter(Task previous, bool enable)
    {
        await previous.ConfigureAwait(true);   // SyncNowAsync never throws: errors become a banner
        await SyncNowAsync(enable).ConfigureAwait(true);
    }

    async Task SyncNowAsync(bool enable)
    {
        var st = _settings.Current;
        string? profile = st.ApplyProfileAtStartup ? st.StartupProfile : null;
        var options = new StartupOptions(st.StartInTray, profile);
        Busy = true;
        try
        {
            await Task.Run(() => { if (enable) _startup.Enable(options); else _startup.Disable(); }).ConfigureAwait(true);
            _settings.Save(_settings.Current with { StartWithWindows = enable });
            string text = !enable ? _l.T("ui.profiles.startupOff")
                : profile == null ? _l.T("ui.startup.on")
                : _l.T("ui.startup.onWithProfile", ("name", profile));
            _banner.Show(text, BannerKind.Success);
        }
        catch (Exception ex)
        {
            // Mostra o estado real da tarefa (pode ter ficado a anterior), nao o que foi pedido.
            bool actual;
            try { actual = await Task.Run(() => _startup.IsEnabled).ConfigureAwait(true); }
            catch (Exception) { actual = !enable; }
            SetEnabledSilently(actual);
            _banner.Show(ResultText.Exception(_l, ex), BannerKind.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    void SetEnabledSilently(bool value)
    {
        _syncing = true;
        _enabled = value;
        OnPropertyChanged(nameof(StartWithWindows));
        _syncing = false;
    }
}
