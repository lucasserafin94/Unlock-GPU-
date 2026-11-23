using System.Windows;
using UnlockGpu.App.ViewModels;
using UnlockGpu.Core.Infrastructure;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UnlockGpu.App.Services;

/// <summary>Actions the tray menu triggers.</summary>
/// <param name="Open">Show and activate the window.</param>
/// <param name="ApplyProfile">Apply a profile by name.</param>
/// <param name="ResetDefaults">Return to driver defaults.</param>
/// <param name="Exit">Quit the app.</param>
/// <param name="ProfileNames">Current profile names (read when the menu opens).</param>
public sealed record TrayActions(Action Open, Func<string, Task> ApplyProfile, Func<Task> ResetDefaults, Action Exit,
    Func<IReadOnlyList<string>> ProfileNames);

/// <summary>
/// Notification-area icon (Windows Forms <see cref="Forms.NotifyIcon"/>) with a power/temperature tooltip and a menu
/// (open, apply profile, reset to defaults, exit). The menu is rebuilt each time it opens, so it is always in the
/// current language and lists the current profiles.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const int MaxTooltip = 127;
    readonly Localizer _l;
    readonly TrayActions _actions;
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu;
    readonly Drawing.Icon _image;
    readonly Drawing.Font _bold;
    bool _disposed;

    /// <summary>Creates and shows the icon.</summary>
    public TrayIcon(Localizer localizer, TrayActions actions)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(actions);
        _l = localizer;
        _actions = actions;
        _image = LoadIcon();
        _menu = new Forms.ContextMenuStrip();
        _bold = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold);
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = Clip(_l.T("app.name")),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _actions.Open(); };
        _icon.BalloonTipClicked += (_, _) => _actions.Open();
        BuildMenu();
    }

    /// <summary>Updates the tooltip (truncated to the Windows limit).</summary>
    public void SetTooltip(string text) { if (!_disposed) _icon.Text = Clip(text); }

    /// <summary>Shows a balloon notification.</summary>
    public void Notify(string text, BannerKind kind)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;
        var icon = kind switch
        {
            BannerKind.Error => Forms.ToolTipIcon.Error,
            BannerKind.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5000, _l.T("app.name"), text.Length > 250 ? text[..250] : text, icon);
    }

    void BuildMenu()
    {
        foreach (Forms.ToolStripItem old in _menu.Items) old.Dispose();
        _menu.Items.Clear();
        var open = new Forms.ToolStripMenuItem(_l.T("ui.tray.open"), null, (_, _) => _actions.Open()) { Font = _bold };
        _menu.Items.Add(open);

        var apply = new Forms.ToolStripMenuItem(_l.T("ui.tray.applyProfile"));
        var names = _actions.ProfileNames();
        if (names.Count == 0)
            apply.DropDownItems.Add(new Forms.ToolStripMenuItem(_l.T("ui.tray.noProfiles")) { Enabled = false });
        foreach (string name in names)
        {
            string n = name;
            apply.DropDownItems.Add(new Forms.ToolStripMenuItem(n, null, async (_, _) => await RunAsync(() => _actions.ApplyProfile(n)).ConfigureAwait(true)));
        }
        _menu.Items.Add(apply);
        _menu.Items.Add(new Forms.ToolStripMenuItem(_l.T("ui.tray.reset"), null, async (_, _) => await RunAsync(_actions.ResetDefaults).ConfigureAwait(true)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem(_l.T("ui.tray.exit"), null, (_, _) => _actions.Exit()));
    }

    async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Notify(ex.Message, BannerKind.Error);
        }
    }

    static Drawing.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/UnlockGPU.ico", UriKind.Absolute));
            if (res != null)
            {
                using var stream = res.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (IOException) { }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    static string Clip(string s) => s.Length <= MaxTooltip ? s : s[..MaxTooltip];

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        foreach (Forms.ToolStripItem item in _menu.Items) item.Dispose();
        _menu.Dispose();
        _bold.Dispose();
        _image.Dispose();
    }
}
