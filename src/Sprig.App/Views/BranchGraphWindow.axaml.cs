using System;
using System.ComponentModel;
using Avalonia.Controls;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

/// <summary>The resizable branch-graph dialog. Bound to the <see cref="WorkspacesViewModel"/> that opened it;
/// it mirrors the VM's <see cref="WorkspacesViewModel.IsBranchGraphOpen"/> — closing the window clears it,
/// and clearing it (via Use/Close) closes the window.</summary>
public partial class BranchGraphWindow : Window
{
    WorkspacesViewModel? _vm;
    bool _closing;

    public BranchGraphWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.ScrollToRowRequested -= ScrollToRow;
        }
        _vm = DataContext as WorkspacesViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.ScrollToRowRequested += ScrollToRow;
        }
    }

    // Bring a commit row into view when the search jumps to a branch (rows vary in height, so we can't just
    // compute an offset — ask the container to reveal itself).
    void ScrollToRow(int index)
    {
        if (RowsHost.ContainerFromIndex(index) is Control container)
            container.BringIntoView();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspacesViewModel.IsBranchGraphOpen)
            && _vm is { IsBranchGraphOpen: false } && !_closing)
        {
            _closing = true;
            Close();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.IsBranchGraphOpen = false; // keep the VM state in step when closed via the X
        }
    }
}
