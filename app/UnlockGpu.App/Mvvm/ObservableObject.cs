using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnlockGpu.App.Mvvm;

/// <summary>Base class for view-models: raises <see cref="PropertyChanged"/> for bound properties.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="propertyName"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises <see cref="PropertyChanged"/> for several properties.</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        foreach (string name in propertyNames) OnPropertyChanged(name);
    }

    /// <summary>Stores <paramref name="value"/> in <paramref name="field"/> and raises the change when it differs.</summary>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
