using System;
using Avalonia.Controls;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class WorkspacesView : UserControl
{
    WorkspacesViewModel? _hooked;

    public WorkspacesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hooked is not null)
        {
            _hooked.OperationStarted -= ShowProgress;
            _hooked.BranchGraphRequested -= ShowBranchGraph;
        }
        _hooked = DataContext as WorkspacesViewModel;
        if (_hooked is not null)
        {
            _hooked.OperationStarted += ShowProgress;
            _hooked.BranchGraphRequested += ShowBranchGraph;
        }
    }

    /// <summary>Open the create/teardown checklist in its own non-blocking window, owned by (but not
    /// blocking) the main window.</summary>
    void ShowProgress(OperationProgressViewModel vm)
    {
        var window = new OperationProgressWindow { DataContext = vm };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    /// <summary>Open the resizable branch-graph dialog, owned by the main window and bound to the same VM.</summary>
    void ShowBranchGraph(WorkspacesViewModel vm)
    {
        var window = new BranchGraphWindow { DataContext = vm };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
}
