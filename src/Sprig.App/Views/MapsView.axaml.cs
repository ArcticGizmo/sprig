using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Sprig.App.Controls;

namespace Sprig.App.Views;

public partial class MapsView : UserControl
{
    public MapsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void FitClicked(object? sender, RoutedEventArgs e) => this.FindControl<MapWiringCanvas>("Canvas")!.FitToView();
}
