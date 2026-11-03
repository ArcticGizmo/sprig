using System;
using Avalonia;
using Avalonia.Controls;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class SproutsView : UserControl
{
    SproutsViewModel? _vm;

    public SproutsView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.ProgressStarted -= ShowProgress;
        _vm = DataContext as SproutsViewModel;
        if (_vm is not null) _vm.ProgressStarted += ShowProgress;
        base.OnDataContextChanged(e);
    }

    void ShowProgress(OperationProgressViewModel progress)
    {
        var window = new OperationProgressWindow { DataContext = progress };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
}
