using UnlockGpu.App.Mvvm;

namespace UnlockGpu.App.ViewModels;

/// <summary>Kind of a banner message (drives its colour).</summary>
public enum BannerKind { Info, Success, Warning, Error }

/// <summary>A dismissable message strip (driver reset, apply results, errors).</summary>
public sealed class BannerViewModel : ObservableObject
{
    static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(12);
    string _text = "";
    BannerKind _kind;
    bool _isVisible;
    int _version;

    /// <summary>Creates the banner.</summary>
    public BannerViewModel() => DismissCommand = new RelayCommand(Dismiss);

    /// <summary>Message text.</summary>
    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    /// <summary>Message kind.</summary>
    public BannerKind Kind { get => _kind; private set => SetProperty(ref _kind, value); }

    /// <summary>Whether the banner is shown.</summary>
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    /// <summary>Hides the banner.</summary>
    public RelayCommand DismissCommand { get; }

    /// <summary>Shows a message. Info and success messages hide themselves after a few seconds.</summary>
    public void Show(string text, BannerKind kind)
    {
        Text = text;
        Kind = kind;
        IsVisible = true;
        int version = ++_version;
        if (kind is BannerKind.Info or BannerKind.Success) _ = HideLaterAsync(version);
    }

    /// <summary>Hides the banner.</summary>
    public void Dismiss()
    {
        _version++;
        IsVisible = false;
    }

    async Task HideLaterAsync(int version)
    {
        await Task.Delay(AutoHide).ConfigureAwait(true);
        if (version == _version) IsVisible = false;
    }
}
