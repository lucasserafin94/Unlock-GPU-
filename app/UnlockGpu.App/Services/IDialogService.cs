using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using UnlockGpu.App.Localization;

namespace UnlockGpu.App.Services;

/// <summary>File dialogs, confirmations and Explorer, kept out of the view-models.</summary>
public interface IDialogService
{
    /// <summary>Shows a save dialog; null when cancelled.</summary>
    string? SaveFile(string title, string filter, string defaultFileName);
    /// <summary>Shows an open dialog; null when cancelled.</summary>
    string? OpenFile(string title, string filter);
    /// <summary>Yes/no question; true for yes.</summary>
    bool Confirm(string title, string message);
    /// <summary>Opens a folder in Explorer.</summary>
    void OpenFolder(string folder);
    /// <summary>Opens Explorer with <paramref name="file"/> selected.</summary>
    void RevealFile(string file);
}

/// <summary>WPF implementation of <see cref="IDialogService"/>.</summary>
public sealed class WpfDialogService : IDialogService
{
    static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    /// <inheritdoc/>
    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, AddExtension = true, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    /// <inheritdoc/>
    public string? OpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    /// <inheritdoc/>
    public bool Confirm(string title, string message)
    {
        var owner = Owner;
        var result = owner != null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    /// <inheritdoc/>
    public void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        StartExplorer("\"" + folder + "\"");
    }

    /// <inheritdoc/>
    public void RevealFile(string file) => StartExplorer("/select,\"" + file + "\"");

    static void StartExplorer(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(TranslationSource.Instance["ui.error.explorer"], TranslationSource.Instance["app.name"]);
        }
    }
}
