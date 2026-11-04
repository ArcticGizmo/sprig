using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sprig.App.Logs;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class SproutsView : UserControl
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    SproutsViewModel? _vm;
    ScrollViewer? _logScroller;
    bool _scrollingToEnd;

    public SproutsView()
    {
        InitializeComponent();
        _poll.Tick += (_, _) =>
        {
            if (_vm is { IsActive: true }) _ = _vm.PollAsync();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        LogList.TemplateApplied += (_, _) => HookLogScroller();
        LogArea.SizeChanged += (_, e) => SetNarrow(e.NewSize.Width < 760);
    }

    /// <summary>Below about 760px the log would be squeezed to a sliver beside the details, so they go over it instead,
    /// closed until asked for; widening again puts them back beside it.</summary>
    void SetNarrow(bool narrow)
    {
        if (LogArea.Classes.Contains("narrow") == narrow) return;
        LogArea.Classes.Set("narrow", narrow);
        if (_vm is not null) _vm.DetailsOpen = !narrow;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ProgressStarted -= ShowProgress;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Logs.ScrollToEnd -= ScrollLogToEnd;
            _vm.Logs.ScrollTo -= ScrollLogTo;
            _vm.Logs.TopRow = null;
        }
        _vm = DataContext as SproutsViewModel;
        if (_vm is not null)
        {
            _vm.ProgressStarted += ShowProgress;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Logs.ScrollToEnd += ScrollLogToEnd;
            _vm.Logs.ScrollTo += ScrollLogTo;
            _vm.Logs.TopRow = TopRow;
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

    // The modal opens ready to type a name; posted so the box is visible before it takes focus.
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SproutsViewModel.IsAddOpen) && _vm?.IsAddOpen == true)
            Dispatcher.UIThread.Post(() => NewNameBox.Focus(), DispatcherPriority.Loaded);
    }

    void HookLogScroller()
    {
        if (_logScroller is not null) return;
        _logScroller = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_logScroller is not null) _logScroller.ScrollChanged += OnLogScrolled;
    }

    /// <summary>Scrolling up from the end pauses the log where it is; scrolling back down to the end follows it again.
    /// Changes in size (lines arriving, the window resizing) aren't the user scrolling, so they're left alone.</summary>
    void OnLogScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || _logScroller is null || _scrollingToEnd || e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) return;
        var atEnd = _logScroller.Offset.Y >= _logScroller.Extent.Height - _logScroller.Viewport.Height - 18;
        if (e.OffsetDelta.Y < 0 && !atEnd) _vm.Logs.SetFollowing(false, TopRow());
        else if (e.OffsetDelta.Y > 0 && atEnd) _vm.Logs.SetFollowing(true);
    }

    void ScrollLogToEnd()
    {
        // After layout, so lines just added have been measured.
        Dispatcher.UIThread.Post(() =>
        {
            HookLogScroller();
            if (_logScroller is null) return;
            _scrollingToEnd = true;
            _logScroller.ScrollToEnd();
            Dispatcher.UIThread.Post(() => _scrollingToEnd = false, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    void ScrollLogTo(LogRow row)
        => Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(row), DispatcherPriority.Background);

    /// <summary>The first line whose top is in view.</summary>
    LogRow? TopRow()
    {
        if (_logScroller is null) return null;
        return LogList.GetRealizedContainers()
            .Select(c => (Row: c.DataContext as LogRow, Y: c.TranslatePoint(new Point(0, 0), _logScroller)?.Y))
            .Where(x => x.Row is not null && x.Y is >= -2)
            .OrderBy(x => x.Y)
            .Select(x => x.Row)
            .FirstOrDefault();
    }

    /// <summary>Up and Down move through the apps, Ctrl+F searches the log, End jumps to its latest line. Typing in a box
    /// keeps its keys.</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { HasSelection: true, OnAppsTab: true } || _vm.IsAddOpen || _vm.ConfirmingRemove) return;
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            LogSearch.Focus();
            LogSearch.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.Up:
                _vm.Step(-1, TopRow());
                e.Handled = true;
                break;
            case Key.Down:
                _vm.Step(1, TopRow());
                e.Handled = true;
                break;
            case Key.End:
                _vm.Logs.JumpToLatestCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    void ToggleProblems(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Logs.ProblemsOnly = !_vm.Logs.ProblemsOnly;
    }

    async void CopyEnv(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(_vm.EnvText());
    }

    void ShowProgress(OperationProgressViewModel progress)
    {
        var window = new OperationProgressWindow { DataContext = progress };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
}
