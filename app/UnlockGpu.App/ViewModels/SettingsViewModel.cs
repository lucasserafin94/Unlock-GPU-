using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>A language choice (native name).</summary>
/// <param name="Code">Two-letter code.</param>
/// <param name="NativeName">Name in its own language.</param>
public sealed record LanguageOption(string Code, string NativeName);

/// <summary>Settings tab: language, accent colour, refresh, graph window, tray and driver-reset behaviour.</summary>
public sealed class SettingsViewModel : TabViewModel
{
    /// <summary>Refresh interval choices (seconds).</summary>
    public static IReadOnlyList<int> RefreshChoices { get; } = [1, 2, 3, 5, 10];

    readonly ISettingsStore _settings;
    readonly ThemeManager _theme;
    readonly IGpuMonitor _monitor;
    LanguageOption _language;
    string _customAccent;
    bool _customAccentInvalid;
    bool _syncing;

    /// <summary>Creates the tab.</summary>
    public SettingsViewModel(Localizer localizer, ISettingsStore settings, ThemeManager theme, IGpuMonitor monitor, StartupViewModel startup) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(startup);
        Startup = startup;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(monitor);
        _settings = settings;
        _theme = theme;
        _monitor = monitor;
        Languages = Localizer.Languages.Select(l => new LanguageOption(l.Code, l.NativeName)).ToArray();
        _language = Languages.FirstOrDefault(l => l.Code == localizer.Code) ?? Languages[0];
        _customAccent = theme.Current;
        SelectAccentCommand = new RelayCommand(p => SetAccent(p as string));
        ApplyCustomAccentCommand = new RelayCommand(() => SetAccent(_customAccent));
    }

    /// <summary>Start with Windows card.</summary>
    public StartupViewModel Startup { get; }

    /// <summary>The 8 languages.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; }

    /// <summary>Selected language; switching is live.</summary>
    public LanguageOption Language
    {
        get => _language;
        set
        {
            if (value == null || !SetProperty(ref _language, value) || _syncing) return;
            L.SetLanguage(value.Code);
            _settings.Save(_settings.Current with { Language = value.Code });
        }
    }

    /// <summary>Accent presets (#RRGGBB).</summary>
    public IReadOnlyList<string> AccentPresets { get; } = ThemeManager.Presets;

    /// <summary>Current accent.</summary>
    public string Accent => _theme.Current;

    /// <summary>Custom accent typed by the user.</summary>
    public string CustomAccent
    {
        get => _customAccent;
        set { if (SetProperty(ref _customAccent, value)) CustomAccentInvalid = false; }
    }

    /// <summary>The typed colour is not valid.</summary>
    public bool CustomAccentInvalid { get => _customAccentInvalid; private set => SetProperty(ref _customAccentInvalid, value); }

    /// <summary>Applies a preset (parameter = hex).</summary>
    public RelayCommand SelectAccentCommand { get; }
    /// <summary>Applies <see cref="CustomAccent"/>.</summary>
    public RelayCommand ApplyCustomAccentCommand { get; }

    /// <summary>Refresh choices.</summary>
    public IReadOnlyList<int> RefreshOptions { get; } = RefreshChoices;

    /// <summary>Monitor refresh (seconds).</summary>
    public int RefreshSeconds
    {
        get => _settings.Current.RefreshSeconds;
        set
        {
            if (!RefreshChoices.Contains(value) || value == _settings.Current.RefreshSeconds) return;
            _monitor.Interval = TimeSpan.FromSeconds(value);
            _settings.Save(_settings.Current with { RefreshSeconds = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Graph window choices.</summary>
    public IReadOnlyList<int> GraphOptions { get; } = MonitorViewModel.GraphChoices;

    /// <summary>Graph window (minutes).</summary>
    public int GraphMinutes
    {
        get => _settings.Current.GraphMinutes;
        set
        {
            if (!MonitorViewModel.GraphChoices.Contains(value) || value == _settings.Current.GraphMinutes) return;
            _settings.Save(_settings.Current with { GraphMinutes = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Minimising hides the window in the tray.</summary>
    public bool MinimizeToTray
    {
        get => _settings.Current.MinimizeToTray;
        set { if (value != _settings.Current.MinimizeToTray) { _settings.Save(_settings.Current with { MinimizeToTray = value }); OnPropertyChanged(); } }
    }

    /// <summary>Reapply the startup/last profile when the driver resets.</summary>
    public bool ReapplyAfterDriverReset
    {
        get => _settings.Current.ReapplyAfterDriverReset;
        set { if (value != _settings.Current.ReapplyAfterDriverReset) { _settings.Save(_settings.Current with { ReapplyAfterDriverReset = value }); OnPropertyChanged(); } }
    }

    /// <inheritdoc/>
    public override void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _syncing = true;
        var lang = Languages.FirstOrDefault(l => l.Code == L.Code);
        if (lang != null && lang != _language) { _language = lang; OnPropertyChanged(nameof(Language)); }
        _syncing = false;
        OnPropertiesChanged(nameof(RefreshSeconds), nameof(GraphMinutes), nameof(MinimizeToTray), nameof(ReapplyAfterDriverReset), nameof(Accent));
        Startup.OnSettingsChanged();
    }

    void SetAccent(string? hex)
    {
        if (!_theme.Apply(hex))
        {
            CustomAccentInvalid = true;
            return;
        }
        _customAccent = _theme.Current;
        CustomAccentInvalid = false;
        OnPropertiesChanged(nameof(CustomAccent), nameof(Accent));
        if (_settings.Current.Accent != _theme.Current) _settings.Save(_settings.Current with { Accent = _theme.Current });
    }
}
