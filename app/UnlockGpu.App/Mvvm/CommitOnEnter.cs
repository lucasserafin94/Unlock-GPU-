using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnlockGpu.App.Mvvm;

/// <summary>
/// Attached behavior that commits a TextBox's explicit binding when the user presses Enter or the box loses focus.
/// Pair it with <c>UpdateSourceTrigger=Explicit</c>: the value is committed once, never per keystroke, and an
/// out-of-range entry can be reverted by reformatting the text from the model.
/// </summary>
public static class CommitOnEnter
{
    /// <summary>Turns the Enter/lost-focus commit on for a TextBox.</summary>
    public static readonly DependencyProperty EnabledProperty =
        // "Enabled" (no member to nameof): attached properties are named after their Get/Set pair.
        // .NET 10 WPF: RegisterAttached lives on DependencyProperty and takes the owner type.
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CommitOnEnter), new PropertyMetadata(false, OnChanged));

    /// <summary>Gets the attached flag from <paramref name="d"/>.</summary>
    [AttachedPropertyBrowsableForType(typeof(TextBox))]
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    /// <summary>Sets the attached flag on <paramref name="d"/>.</summary>
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        if ((bool)e.NewValue)
        {
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.LostFocus += OnLostFocus;
        }
        else
        {
            box.PreviewKeyDown -= OnPreviewKeyDown;
            box.LostFocus -= OnLostFocus;
        }
    }

    static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box) Commit(box);
    }

    static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) Commit(box);
    }

    /// <summary>Pushes the current text into its binding source.</summary>
    public static void Commit(TextBox box) => box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
}
