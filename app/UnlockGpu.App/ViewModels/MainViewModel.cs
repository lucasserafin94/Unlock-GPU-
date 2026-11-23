using System.Reflection;
using UnlockGpu.App.Hosting;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>
/// Shell view-model: owns the tabs, marshals monitor/stability/settings events to the UI thread, keeps the header,
/// health light and main banner, and reapplies the startup/last profile after a driver reset.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    static readonly TimeSpan AverageWindow = TimeSpan.FromSeconds(30);

    readonly AppServices _s;
    readonly IUiDispatcher _ui;
    readonly ThemeManager _theme;
    readonly Localizer _l;
    readonly TabViewModel[] _tabs;
    GpuSample? _latest;
    // Ultima info nao nula lida fora da thread de UI (StartAsync ou thread do monitor); o header so usa este cache.
    GpuInfo? _info;
    bool _infoChecked;
    string _gpuName = "";
    string _headerDetail = "";
    string _trayText = "";
    Health _health = Health.Good;
    string _healthText = "";
    bool _disposed;
    // A driver reset seen while the auto-tuner runs: the profile is reapplied only after the tuner has finished
    // (and restored or applied its offset), so the two never write offsets over each other mid-run.
    bool _reapplyAfterTune;

    /// <summary>Creates the shell and all tabs.</summary>
    public MainViewModel(AppServices services, Localizer localizer, IUiDispatcher ui, IDialogService dialogs, ThemeManager theme)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(theme);
        _s = services;
        _l = localizer;
        _ui = ui;
        _theme = theme;

        Monitor = new MonitorViewModel(localizer, services.Settings, services.Gpu);
        Tuning = new TuningViewModel(localizer, services.Gpu, services.Offsets, services.Vram, services.Power, services.Settings, Banner);
        AutoTune = new AutoTuneViewModel(localizer, services.Tuner, services.Offsets, services.Stability, Banner);
        Sessions = new SessionsViewModel(localizer, services.Recorder, dialogs, services.Paths, Banner);
        Profiles = new ProfilesViewModel(localizer, services.Profiles, services.Applier, services.Backup,
            services.Settings, dialogs, services.UiState, Banner);
        Oem = new OemViewModel(localizer, services.Oem, services.Gpu);
        Rom = new RomViewModel(localizer, services.RomOverride, dialogs);
        Startup = new StartupViewModel(localizer, services.Startup, services.Profiles, services.Settings, Banner);
        Drift = new DriftViewModel(localizer, services.Profiles, services.Settings, services.UiState,
            name => Profiles.ApplyByNameAsync(name), Profiles.ResetToDefaultsAsync);
        Settings = new SettingsViewModel(localizer, services.Settings, theme, services.Monitor, Startup);
        _tabs = [Monitor, Tuning, AutoTune, Sessions, Profiles, Oem, Rom, Settings];

        VersionText = "v" + AppVersion();
        _s.Monitor.SampleArrived += OnSampleArrived;
        _s.Monitor.DriverReset += OnDriverResetBackground;
        _s.Stability.Changed += OnHealthChanged;
        _s.Settings.Changed += OnSettingsSaved;
        _l.Changed += OnLanguageChanged;
        Profiles.Applied += OnHardwareChanged;
        Profiles.ProfilesChanged += OnProfilesChanged;
        AutoTune.OffsetsChanged += OnHardwareChanged;
        UpdateHealth(_s.Stability.Current);
        UpdateHeader();
    }

    /// <summary>Main message strip.</summary>
    public BannerViewModel Banner { get; } = new();
    /// <summary>Status bar: hardware versus the active profile, with Apply / Reset.</summary>
    public DriftViewModel Drift { get; }
    /// <summary>Monitor tab.</summary>
    public MonitorViewModel Monitor { get; }
    /// <summary>Tuning tab.</summary>
    public TuningViewModel Tuning { get; }
    /// <summary>Auto-tune tab.</summary>
    public AutoTuneViewModel AutoTune { get; }
    /// <summary>Sessions tab.</summary>
    public SessionsViewModel Sessions { get; }
    /// <summary>Profiles tab.</summary>
    public ProfilesViewModel Profiles { get; }
    /// <summary>OEM tab.</summary>
    public OemViewModel Oem { get; }
    /// <summary>vBIOS offset kit (romOverride) tab.</summary>
    public RomViewModel Rom { get; }
    /// <summary>Settings tab.</summary>
    public SettingsViewModel Settings { get; }
    /// <summary>Start with Windows card (shown in the Settings tab).</summary>
    public StartupViewModel Startup { get; }

    /// <summary>"v4.0.0".</summary>
    public string VersionText { get; }
    /// <summary>GPU name.</summary>
    public string GpuName { get => _gpuName; private set => SetProperty(ref _gpuName, value); }
    /// <summary>Driver, vBIOS and P-state.</summary>
    public string HeaderDetail { get => _headerDetail; private set => SetProperty(ref _headerDetail, value); }
    /// <summary>Tray tooltip (power and temperature).</summary>
    public string TrayText { get => _trayText; private set => SetProperty(ref _trayText, value); }
    /// <summary>Health light level.</summary>
    public Health HealthLevel { get => _health; private set => SetProperty(ref _health, value); }
    /// <summary>Health tooltip (translated reasons).</summary>
    public string HealthText { get => _healthText; private set => SetProperty(ref _healthText, value); }

    /// <summary>Starts monitoring and reads the slow things (offsets, power support, startup task, OEM) in the background.</summary>
    public async Task StartAsync()
    {
        int secs = _s.Settings.Current.RefreshSeconds;
        _s.Monitor.Interval = TimeSpan.FromSeconds(SettingsViewModel.RefreshChoices.Contains(secs) ? secs : 2);
        bool available = await Task.Run(() => _s.Gpu.IsAvailable).ConfigureAwait(true);
        _s.Monitor.Start();
        if (!available)
            Banner.Show(_l.T("ui.error.gpuUnavailable", ("error", _s.Gpu.LastError)), BannerKind.Error);
        var info = await Task.Run(() => _s.Gpu.Info).ConfigureAwait(true);
        if (info != null) _info = info;
        _infoChecked = true;
        UpdateHeader();
        if (available) await Tuning.InitializeAsync().ConfigureAwait(true);
        await Startup.InitializeAsync().ConfigureAwait(true);
        await Oem.RefreshCommand.ExecuteAsync().ConfigureAwait(true);
        await Rom.RefreshCommand.ExecuteAsync().ConfigureAwait(true);
    }

    /// <summary>Applies a profile by name (tray menu).</summary>
    public Task ApplyProfileAsync(string name) => Profiles.ApplyByNameAsync(name);

    /// <summary>Back to driver defaults (tray menu).</summary>
    public Task ResetToDefaultsAsync() => Profiles.ResetToDefaultsAsync();

    // ---------------- background → UI ----------------

    void OnSampleArrived(object? sender, GpuSample sample)
    {
        // Monitor thread: do the per-sample work here so the UI thread only formats text.
        VramLockState? lockState = null;
        GpuInfo? info = null;
        try
        {
            // IGpu.Info pode rodar nvmlInit e esperar o lock do NVML: le aqui, nunca na thread de UI.
            info = _s.Gpu.Info;
            if (sample.Ok) lockState = _s.Vram.Evaluate(sample);
            _s.Recorder.Add(sample);
            _s.Stability.Feed(sample);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _s.Log.Error("UI sample processing", ex);
        }
        var history = _s.Monitor.History;
        var ctx = new SampleContext(sample, lockState, Average(history, sample.Time), history, info);
        _ui.Post(() => Dispatch(ctx));
    }

    void Dispatch(SampleContext ctx)
    {
        if (_disposed) return;
        _latest = ctx.Sample;
        if (ctx.Info != null) _info = ctx.Info;
        _infoChecked = true;
        foreach (var tab in _tabs) tab.OnSample(ctx);
        Drift.OnSample(ctx);
        UpdateHeader();
        var s = ctx.Sample;
        TrayText = s.Ok
            ? _l.T("ui.tray.tooltip", ("power", Format.WattsWhole(s.PowerW, _l.Culture)), ("temp", s.TempC))
            : _l.T("app.name");
    }

    void OnHealthChanged(object? sender, HealthReport report) => _ui.Post(() => UpdateHealth(report));

    void OnSettingsSaved(object? sender, AppSettings settings) => _ui.Post(() => ApplySettings(settings));

    void OnDriverResetBackground(object? sender, EventArgs e) => _ui.Post(() => _ = OnDriverResetAsync());

    async Task OnDriverResetAsync()
    {
        if (_disposed) return;
        try
        {
            await Tuning.ReloadOffsetsAsync().ConfigureAwait(true);
            var st = _s.Settings.Current;
            if (!st.ReapplyAfterDriverReset)
            {
                Banner.Show(_l.T("ui.reset.detected"), BannerKind.Warning);
                return;
            }
            if (AutoTune.IsRunning)
            {
                _reapplyAfterTune = true;
                _s.Log.Info("UI driver reset during auto-tune: profile reapply deferred until the run ends");
                Banner.Show(_l.T("ui.reset.detected"), BannerKind.Warning);
                return;
            }
            string? name = (st.ApplyProfileAtStartup ? st.StartupProfile : null) ?? _s.UiState.Current.LastProfile;
            if (string.IsNullOrWhiteSpace(name))
            {
                Banner.Show(_l.T("ui.reset.nothingToReapply"), BannerKind.Warning);
                return;
            }
            var results = await Profiles.ApplyByNameAsync(name).ConfigureAwait(true);
            if (results == null) return;
            bool ok = results.All(r => r.Ok);
            Banner.Show(_l.T("ui.reset.reapplied", ("name", name)) + Environment.NewLine + ResultText.Describe(_l, results),
                ok ? BannerKind.Warning : BannerKind.Error);
        }
        catch (Exception ex)
        {
            _s.Log.Error("UI driver reset reapply", ex);
            Banner.Show(ResultText.Exception(_l, ex), BannerKind.Error);
        }
    }

    void OnHardwareChanged(object? sender, EventArgs e)
    {
        if (_reapplyAfterTune && !AutoTune.IsRunning && !_disposed)
        {
            _reapplyAfterTune = false;
            _ = OnDriverResetAsync();
            return;
        }
        Drift.ReloadProfile();
        _ = ReloadOffsetsSafeAsync();
    }

    async Task ReloadOffsetsSafeAsync()
    {
        try { await Tuning.ReloadOffsetsAsync().ConfigureAwait(true); }
        catch (Exception ex) { _s.Log.Error("UI reload offsets", ex); }
    }

    void OnProfilesChanged(object? sender, EventArgs e)
    {
        Startup.RefreshProfiles();
        Drift.ReloadProfile();
    }

    void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var tab in _tabs) tab.OnLanguageChanged();
        Drift.OnLanguageChanged();
        UpdateHealth(_s.Stability.Current);
        UpdateHeader();
    }

    void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;
        if (!string.IsNullOrEmpty(settings.Language) && settings.Language != _l.Code) _l.SetLanguage(settings.Language);
        if (!string.Equals(settings.Accent, _theme.Current, StringComparison.OrdinalIgnoreCase)) _theme.Apply(settings.Accent);
        if (SettingsViewModel.RefreshChoices.Contains(settings.RefreshSeconds)
            && _s.Monitor.Interval != TimeSpan.FromSeconds(settings.RefreshSeconds))
            _s.Monitor.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        foreach (var tab in _tabs) tab.OnSettingsChanged(settings);
        Drift.ReloadProfile();
    }

    void UpdateHealth(HealthReport report)
    {
        HealthLevel = report.Level;
        HealthText = report.ReasonKeys.Count == 0
            ? _l.T("ui.health.good")
            : _l.T(report.Level == Health.Critical ? "ui.health.critical" : "ui.health.warning") + Environment.NewLine
              + string.Join(Environment.NewLine, report.ReasonKeys.Select(k => "• " + _l.T(k)));
    }

    void UpdateHeader()
    {
        // So usa o cache: nada de IGpu.Info aqui (thread de UI). Antes da primeira leitura mostra um placeholder.
        var info = _info;
        GpuName = info?.Name ?? (_infoChecked ? _l.T("ui.header.noGpu") : Format.Unknown);
        HeaderDetail = info == null ? "" : _l.T("ui.header.detail", ("driver", info.Driver), ("vbios", info.Vbios),
            ("pstate", _latest is { Ok: true } s ? s.PState : Format.Unknown));
    }

    static double Average(IReadOnlyList<GpuSample> history, DateTime now)
    {
        double sum = 0;
        int n = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var h = history[i];
            if (now - h.Time > AverageWindow) break;
            if (!h.Ok || double.IsNaN(h.PowerW)) continue;
            sum += h.PowerW;
            n++;
        }
        return n == 0 ? double.NaN : sum / n;
    }

    static string AppVersion()
    {
        var asm = typeof(MainViewModel).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v)) return asm.GetName().Version?.ToString(3) ?? "?";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Exit during an auto-tune run: let the tuner put the original offset back before the GPU is released.
        if (!AutoTune.CancelAndWait(TimeSpan.FromSeconds(10))) _s.Log.Warn("UI exit: auto-tune did not finish restoring in time");
        _s.Monitor.SampleArrived -= OnSampleArrived;
        _s.Monitor.DriverReset -= OnDriverResetBackground;
        _s.Stability.Changed -= OnHealthChanged;
        _s.Settings.Changed -= OnSettingsSaved;
        _l.Changed -= OnLanguageChanged;
        Profiles.Applied -= OnHardwareChanged;
        Profiles.ProfilesChanged -= OnProfilesChanged;
        AutoTune.OffsetsChanged -= OnHardwareChanged;
        _s.Monitor.Stop();
    }
}
