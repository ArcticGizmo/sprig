using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class CodeReviewView : UserControl
{
    CodeReviewViewModel? _vm;

    public CodeReviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as CodeReviewViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnChanged;
        };
    }

    // Focus goes into the dialog when it opens, so Escape works straight away; on Close rather than Approve, so a stray
    // Enter can't approve code nobody has read. Posted so the dialog is visible first.
    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeReviewViewModel.IsOpen) && _vm?.IsOpen == true)
            Dispatcher.UIThread.Post(() => CloseButton.Focus(), DispatcherPriority.Loaded);
    }
}
