using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class OrchestrationsView : UserControl
{
    OrchestrationsViewModel? _vm;

    public OrchestrationsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as OrchestrationsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        };
    }

    // Each modal opens with focus inside it, so typing and Escape work at once; posted so it's visible before it takes focus.
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrchestrationsViewModel.IsAddOpen) && _vm?.IsAddOpen == true)
            Dispatcher.UIThread.Post(() => AddPath.FocusInput(), DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(OrchestrationsViewModel.IsChooseOpen) && _vm?.IsChooseOpen == true)
            Dispatcher.UIThread.Post(() => ChooseExisting.Focus(), DispatcherPriority.Loaded);
    }
}
