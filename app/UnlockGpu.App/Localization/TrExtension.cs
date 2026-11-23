using System.Windows.Data;
using System.Windows.Markup;

namespace UnlockGpu.App.Localization;

/// <summary>XAML shortcut for a live-translated text: <c>Text="{l:Tr ui.tab.monitor}"</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    /// <summary>Creates an empty extension (XAML).</summary>
    public TrExtension() { }

    /// <summary>Creates the extension for <paramref name="key"/>.</summary>
    public TrExtension(string key) => Key = key;

    /// <summary>The Localizer key.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding("[" + Key + "]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
