using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UnlockGpu.App.Hosting;
using UnlockGpu.App.Localization;
using UnlockGpu.App.Services;
using UnlockGpu.App.ViewModels;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App;

/// <summary>
/// Composition root. Parses the command line (<c>--silent --apply-profile "name"</c>, <c>--minimized</c>), enforces a
/// single interactive instance, builds the services, view-models, window and tray icon, and tears everything down in
/// order on exit.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Application lifetime; everything is disposed in OnExit.")]
public partial class App : Application
{
    FileLog? _log;
    SingleInstance? _instance;
    AppServices? _services;
    MainViewModel? _vm;
    MainWindow? _window;
    TrayIcon? _tray;
    bool _exiting;

    /// <inheritdoc/>
    protected override async void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        var options = CommandLineOptions.Parse(e.Args);
        var paths = new AppPaths();
        var log = new FileLog(paths);
        _log = log;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        if (options.Unknown.Count > 0) log.Warn("Ignored arguments: " + string.Join(' ', options.Unknown));

        if (options.Silent)
        {
            int code = await new SilentProfileRunner(paths, log).RunAsync(options.ApplyProfile, CancellationToken.None).ConfigureAwait(true);
            Shutdown(code);
            return;
        }

        _instance = SingleInstance.TryAcquire();
        if (_instance == null)
        {
            log.Info("Another instance is running; asked it to show its window");
            Shutdown(0);
            return;
        }

        try
        {
            StartInteractive(paths, log, options);
            await _vm!.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.Error("Startup failed", ex);
            MessageBox.Show(TranslationSource.Instance["ui.error.startup"] + Environment.NewLine + ex.Message,
                TranslationSource.Instance["app.name"], MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
            return;
        }

        // A failing --apply-profile is not a startup failure: report it and keep the app running.
        if (options.ApplyProfile != null && !_exiting)
        {
            try { await _vm.ApplyProfileAsync(options.ApplyProfile).ConfigureAwait(true); }
            catch (Exception ex)
            {
                log.Error("Startup profile apply failed", ex);
                _vm.Banner.Show(TranslationSource.Instance["ui.error.unhandled"] + " " + ex.Message, BannerKind.Error);
            }
        }
    }

    void StartInteractive(AppPaths paths, FileLog log, CommandLineOptions options)
    {
        var settings = new SettingsStore(paths);
        string? language = string.IsNullOrEmpty(settings.Current.Language) ? null : settings.Current.Language;
        var localizer = new Localizer(language);
        TranslationSource.Instance.Attach(localizer);

        var theme = new ThemeManager(Resources);
        if (!theme.Apply(settings.Current.Accent)) theme.Apply(ThemeManager.DefaultAccent);

        _services = new AppServices(paths, log, settings);
        var vm = new MainViewModel(_services, localizer, new WpfUiDispatcher(Dispatcher), new WpfDialogService(), theme);
        _vm = vm;
        _window = new MainWindow(vm, settings, ExitApp);
        MainWindow = _window;

        _tray = new TrayIcon(localizer, new TrayActions(
            Open: ShowWindow,
            ApplyProfile: vm.ApplyProfileAsync,
            ResetDefaults: vm.ResetToDefaultsAsync,
            Exit: ExitApp,
            ProfileNames: vm.Profiles.ListNames));
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Banner.PropertyChanged += OnBannerChanged;
        _instance!.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(ShowWindow);

        if (!options.Minimized) _window.Show();
        log.Info($"UI started (minimized={options.Minimized}, language={localizer.Code})");
    }

    void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TrayText) && _vm != null) _tray?.SetTooltip(_vm.TrayText);
    }

    void OnBannerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When the window is hidden in the tray, important messages (driver reset, apply results) become balloons.
        if (e.PropertyName != nameof(BannerViewModel.Text) || _vm == null || _window is { IsVisible: true }) return;
        var b = _vm.Banner;
        if (b.IsVisible) _tray?.Notify(b.Text, b.Kind);
    }

    void ShowWindow()
    {
        if (_exiting) return;
        _window?.Reveal();
    }

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { _window?.CloseForExit(); }
        finally { Shutdown(0); }
    }

    /// <inheritdoc/>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        _log?.Info("Windows session ending");
        ExitApp();
    }

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnShellPropertyChanged;
            _vm.Banner.PropertyChanged -= OnBannerChanged;
        }
        _tray?.Dispose();
        _vm?.Dispose();
        _services?.Dispose();
        _instance?.Dispose();
        _log?.Info("UI exit");
        base.OnExit(e);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        if (_vm != null)
        {
            _vm.Banner.Show(TranslationSource.Instance["ui.error.unhandled"] + " " + e.Exception.Message, BannerKind.Error);
            e.Handled = true;
        }
    }

    void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _log?.Error("Unhandled exception", e.ExceptionObject as Exception);

    void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
