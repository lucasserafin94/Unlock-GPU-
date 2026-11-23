using System.Windows.Controls;

namespace UnlockGpu.App.Views;

/// <summary>Dismissable message strip bound to a <see cref="ViewModels.BannerViewModel"/>.</summary>
public partial class BannerView : UserControl
{
    /// <summary>Creates the view.</summary>
    public BannerView() => InitializeComponent();
}
