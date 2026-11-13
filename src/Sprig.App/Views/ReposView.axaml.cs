using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class ReposView : UserControl
{
    ReposViewModel? _vm;

    public ReposView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as ReposViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        };
    }

    // The modal opens ready to type a path; posted so the box is visible before it takes focus.
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReposViewModel.IsAddOpen) && _vm?.IsAddOpen == true)
            Dispatcher.UIThread.Post(() => AddPath.FocusInput(), DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(ReposViewModel.IsChooseOpen) && _vm?.IsChooseOpen == true)
            Dispatcher.UIThread.Post(() => ChooseInRepo.Focus(), DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(ReposViewModel.IsRenameOpen) && _vm is { IsRenameOpen: true, RenameBlocked: false })
            Dispatcher.UIThread.Post(() =>
            {
                RenameBox.Focus();
                RenameBox.SelectAll();
            }, DispatcherPriority.Loaded);
    }
}
