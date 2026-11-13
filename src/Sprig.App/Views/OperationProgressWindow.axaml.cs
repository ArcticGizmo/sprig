using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

/// <summary>Shown with <c>Show(owner)</c>, so it floats over the main window without blocking it. It closes only once
/// the operation has finished, so a half-done step is never hidden; closing it before then cancels the operation
/// instead, and the window stays to say how far it got.</summary>
public partial class OperationProgressWindow : Window
{
    public OperationProgressWindow() => InitializeComponent();

    void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is OperationProgressViewModel { CanClose: false } running && !e.IsProgrammatic)
        {
            e.Cancel = true;
            running.Cancel();
        }
        base.OnClosing(e);
    }

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
