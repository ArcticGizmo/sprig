using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sprig.App.Views;

public partial class MapsView : UserControl
{
    public MapsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
