using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sprig.App.Controls;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class MapsView : UserControl
{
    public MapsView()
    {
        InitializeComponent();
        // A new map's graph opens scaled to the pane, so its whole shape is visible before any scrolling.
        this.FindControl<MapWiringCanvas>("Canvas")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == MapWiringCanvas.GraphProperty)
                Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    string? _fittedMap;

    void FitClicked(object? sender, RoutedEventArgs e) => Fit(force: true);

    void Fit() => Fit(force: false);

    // Fits once per map: a fix redraws the graph, and the zoom shouldn't jump under the user's eye.
    void Fit(bool force)
    {
        if (DataContext is not MapsViewModel vm || vm.Wiring.Layout is not { } layout || layout.Size.Width <= 0) return;
        if (!force && vm.Selected?.Name == _fittedMap) return;
        _fittedMap = vm.Selected?.Name;
        var viewport = this.FindControl<ScrollViewer>("GraphScroll")!.Bounds.Width - 4;
        if (viewport <= 0) return;
        vm.Wiring.Zoom = Math.Clamp(Math.Round(viewport / layout.Size.Width, 2), 0.3, 1);
    }
}
