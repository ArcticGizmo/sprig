using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class SproutsView : UserControl
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    SproutsViewModel? _vm;

    public SproutsView()
    {
        InitializeComponent();
        _poll.Tick += (_, _) =>
        {
            if (_vm is { IsActive: true }) _ = _vm.PollAsync();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ProgressStarted -= ShowProgress;
            _vm.Log.LinesAdded -= KeepNewestInView;
        }
        _vm = DataContext as SproutsViewModel;
        if (_vm is not null)
        {
            _vm.ProgressStarted += ShowProgress;
            _vm.Log.LinesAdded += KeepNewestInView;
        }
        base.OnDataContextChanged(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _poll.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _poll.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Scrolls the log's own scroller only: ScrollIntoView would also scroll the page to bring the line into
    /// view, dragging the whole page down with every new line.</summary>
    void KeepNewestInView()
    {
        if (_vm?.Log is not { Follow: true } log || log.Lines.Count == 0) return;
        // After layout, so the new lines have been measured.
        Dispatcher.UIThread.Post(() => LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    void ShowProgress(OperationProgressViewModel progress)
    {
        var window = new OperationProgressWindow { DataContext = progress };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
}
