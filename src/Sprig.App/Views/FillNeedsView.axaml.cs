using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class FillNeedsView : UserControl
{
    FillNeedsViewModel? _vm;

    public FillNeedsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as FillNeedsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnChanged;
        };
    }

    // Focus goes into the dialog when it opens, so Escape works straight away; on Close, so a stray Enter writes nothing.
    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FillNeedsViewModel.IsOpen) && _vm?.IsOpen == true)
            Dispatcher.UIThread.Post(() => CloseButton.Focus(), DispatcherPriority.Loaded);
    }
}
