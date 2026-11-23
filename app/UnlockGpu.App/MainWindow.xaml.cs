using System.Windows;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App;

/// <summary>
/// Main window: borderless with a custom title bar (dragging comes from WindowChrome). Minimise hides to the tray when
/// the setting is on; close exits the app through the shutdown callback.
/// </summary>
public partial class MainWindow : Window
{
    readonly ISettingsStore _settings;
    readonly Action _exit;
    bool _closingForExit;

    /// <summary>Creates the window over its view-model.</summary>
    /// <param name="viewModel">Shell view-model (DataContext).</param>
    /// <param name="settings">Settings (minimise-to-tray).</param>
    /// <param name="exit">Called when the user closes the window.</param>
    public MainWindow(object viewModel, ISettingsStore settings, Action exit)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(exit);
        _settings = settings;
        _exit = exit;
        InitializeComponent();
        DataContext = viewModel;
        FitToWorkArea();
    }

    /// <summary>
    /// The window is not resizable, so on a short work area (e.g. 1080p at 150 % scaling) its fixed height would push
    /// the footer off screen. The tabs scroll, so shrinking the height to the work area keeps everything reachable.
    /// </summary>
    void FitToWorkArea()
    {
        double available = SystemParameters.WorkArea.Height;
        if (available <= 0) return;
        if (MinHeight > available) MinHeight = available;
        if (Height > available) Height = available;
    }

    /// <summary>Shows, restores and focuses the window.</summary>
    public void Reveal()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Closes the window for real (called by the app during shutdown).</summary>
    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    /// <inheritdoc/>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray) Hide();
    }

    /// <inheritdoc/>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closingForExit) return;
        // Alt+F4 and the close button both go through the app's orderly shutdown.
        e.Cancel = true;
        _exit();
    }

    void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnCloseClick(object sender, RoutedEventArgs e) => _exit();
}
