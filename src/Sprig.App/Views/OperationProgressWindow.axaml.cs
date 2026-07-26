using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

/// <summary>
/// Non-modal window that shows a live create/teardown checklist. Opened with <c>.Show(owner)</c> so it
/// floats above the main window without blocking it. The Close button is gated on
/// <see cref="OperationProgressViewModel.CanClose"/> (enabled once the operation has finished as far as
/// it can); Escape closes only once closing is allowed.
/// </summary>
public partial class OperationProgressWindow : Window
{
    public OperationProgressWindow() => InitializeComponent();

    void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is OperationProgressViewModel { CanClose: true })
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
