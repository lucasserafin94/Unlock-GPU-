using System.ComponentModel;
using System.Globalization;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.App.Localization;

/// <summary>
/// Bindable view of the <see cref="Localizer"/>: XAML binds to the indexer (<c>[key]</c>) and every binding refreshes
/// when the language changes, so the UI switches language without a restart.
/// </summary>
/// <remarks>
/// <see cref="Instance"/> is the one documented piece of static state in the UI: XAML markup extensions have no
/// constructor injection, so the composition root attaches the app's Localizer once at startup.
/// </remarks>
public sealed class TranslationSource : INotifyPropertyChanged
{
    Localizer? _localizer;

    /// <summary>The instance XAML binds to (see remarks).</summary>
    public static TranslationSource Instance { get; } = new();

    TranslationSource() { }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Translated text for <paramref name="key"/> (the key itself when nothing is attached yet).</summary>
    public string this[string key] => _localizer?.T(key) ?? key;

    /// <summary>Culture of the current language (number formatting in the UI).</summary>
    public CultureInfo Culture => _localizer?.Culture ?? CultureInfo.InvariantCulture;

    /// <summary>Connects the localizer. Called once by the composition root.</summary>
    public void Attach(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (_localizer != null) _localizer.Changed -= OnChanged;
        _localizer = localizer;
        _localizer.Changed += OnChanged;
        OnChanged(this, EventArgs.Empty);
    }

    void OnChanged(object? sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }
}
