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

public partial class WorkspacesView : UserControl
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    WorkspacesViewModel? _vm;
    ScrollViewer? _logScroller;
    bool _scrolling;
    readonly MenuItem _copyLines = new() { InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
    readonly MenuItem _allAt = new();
    readonly MenuItem _appAt = new();
    LogRow? _menuRow;
    int _dragFrom = -1;
    bool _dragged;

    public WorkspacesView()
    {
        InitializeComponent();
        _poll.Tick += (_, _) =>
        {
            if (_vm is { IsActive: true }) _ = _vm.PollAsync();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        LogList.TemplateApplied += (_, _) => HookLogScroller();
        LogArea.SizeChanged += (_, e) => SetNarrow(e.NewSize.Width < 760);

        _copyLines.Click += (_, _) => CopySelectedLines();
        _allAt.Click += (_, _) =>
        {
            if (_menuRow is { } row) _vm?.OpenAllAt(row, TopRow());
        };
        _appAt.Click += (_, _) =>
        {
            if (_menuRow is { } row) _vm?.OpenAppAt(row, TopRow());
        };
        LogList.ContextMenu = new ContextMenu { Items = { _copyLines, _allAt, _appAt } };
        LogList.AddHandler(ContextRequestedEvent, OnLogContext, RoutingStrategies.Tunnel);
        LogList.AddHandler(PointerPressedEvent, OnLogPressed, RoutingStrategies.Tunnel);
        LogList.AddHandler(PointerMovedEvent, OnLogDragged, RoutingStrategies.Tunnel);
        LogList.AddHandler(PointerReleasedEvent, OnLogReleased, RoutingStrategies.Tunnel);
        LogList.SelectionChanged += OnLogSelectionChanged;
        AddHandler(PointerReleasedEvent, OnLinkReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(ContextRequestedEvent, OnLinkContext, RoutingStrategies.Tunnel);
        // The links menu is a popup of its own, outside this view's tree, so its links are handled where they are.
        if (LinksButton.Flyout is Flyout { Content: Control links })
        {
            links.AddHandler(PointerReleasedEvent, OnLinkReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            links.AddHandler(ContextRequestedEvent, OnLinkContext, RoutingStrategies.Tunnel);
        }
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenLink(_menuUrl, newWindow: false);
        var window = new MenuItem { Header = "Open in a new window" };
        window.Click += (_, _) => OpenLink(_menuUrl, newWindow: true);
        var copy = new MenuItem { Header = "Copy link" };
        copy.Click += async (_, _) =>
        {
            if (_menuUrl is { } url && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
        };
        _linkMenu = new ContextMenu { Items = { open, window, copy } };

        _appStart.Click += (_, _) =>
        {
            if (_menuApp is { } app) _vm?.StartAppCommand.Execute(app);
        };
        _appStop.Click += (_, _) =>
        {
            if (_menuApp is { } app) _vm?.StopAppCommand.Execute(app);
        };
        _appPin.Click += (_, _) =>
        {
            if (_menuApp is { } app) _vm?.TogglePinCommand.Execute(app);
        };
        _appMenu = new ContextMenu();
        AppList.AddHandler(ContextRequestedEvent, OnAppContext, RoutingStrategies.Tunnel);
        AppList.AddHandler(PointerPressedEvent, OnAppPressed, RoutingStrategies.Tunnel);

        _marksStart.Click += (_, _) => _vm?.StartMarkedCommand.Execute(null);
        _marksRestart.Click += (_, _) => _vm?.RestartMarkedCommand.Execute(null);
        _marksStop.Click += (_, _) => _vm?.StopMarkedCommand.Execute(null);
        _marksPin.Click += (_, _) => _vm?.PinMarkedCommand.Execute(null);
        _marksMenu = new ContextMenu { Items = { _marksStart, _marksRestart, _marksStop, new Separator(), _marksPin } };

        GraphCanvas.NodeMenuRequested += ShowNodeMenu;

        foreach (var box in new[] { NewExistingBox, NewFromBox })
        {
            // Filtered by what's typed, but a branch already picked shows the whole list again, so another can be chosen.
            box.ItemFilter = (search, item) => string.IsNullOrEmpty(search) || item is not Sprig.Engine.Workspaces.BranchChoice choice
                || _vm?.NewBranchChoices.Any(c => c.Ref == search) == true
                || choice.Ref.Contains(search, StringComparison.OrdinalIgnoreCase);
            // A click in the box lists what can be picked, so it reads as a choice rather than a blank to fill.
            box.AddHandler(PointerReleasedEvent, (_, _) =>
            {
                if (!box.IsDropDownOpen && box.ItemsSource is System.Collections.ICollection { Count: > 0 }) box.IsDropDownOpen = true;
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    /// <summary>The ▾ beside a branch picker: every branch, or closed again.</summary>
    void OpenBranchList(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string name || this.FindControl<AutoCompleteBox>(name) is not { } box) return;
        box.Focus();
        box.IsDropDownOpen = !box.IsDropDownOpen;
    }

    /// <summary>A graph node's actions: its log, start or restart, stop, and an app's pages.</summary>
    void ShowNodeMenu(GraphNode node, Control at)
    {
        if (_vm is not { } vm) return;
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        void Add(string header, Action act)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        if (node.HasLog) Add("Open log", () => vm.OpenNodeLog(node));
        if (node.CanStart) Add(node.StartLabel, () => vm.GraphStartCommand.Execute(node));
        if (node.CanStop) Add("Stop", () => vm.GraphStopCommand.Execute(node));
        var links = node.App is { } row ? vm.LinksOf(row) : [];
        if (links.Count > 0 && menu.Items.Count > 0) menu.Items.Add(new Separator());
        foreach (var link in links) menu.Items.Add(LinkItem(menu, link.Label, link.Url));
        if (menu.Items.Count == 0) return;
        menu.Open(at);
    }

    /// <summary>A page's item in a menu, opened as a link is: a click in the browser, a middle-click in a new window.</summary>
    MenuItem LinkItem(ContextMenu menu, string label, string url)
    {
        var item = new MenuItem { Header = $"{label} ↗" };
        ToolTip.SetTip(item, url);
        item.Click += (_, _) => OpenLink(url, newWindow: false);
        item.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Middle) return;
            e.Handled = true;
            menu.Close();
            OpenLink(url, newWindow: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return item;
    }

    readonly ContextMenu _linkMenu;
    string? _menuUrl;

    // The app list is too narrow for buttons on every row, so an app's actions live on one menu.
    readonly ContextMenu _appMenu;
    readonly MenuItem _appStart = new();
    readonly MenuItem _appStop = new() { Header = "Stop" };
    readonly MenuItem _appPin = new();
    WorkspaceAppRow? _menuApp;

    readonly ContextMenu _marksMenu;
    readonly MenuItem _marksStart = new();
    readonly MenuItem _marksRestart = new();
    readonly MenuItem _marksStop = new();
    readonly MenuItem _marksPin = new();

    /// <summary>Built each time it opens, from the workspace's plan as it is now, with the current choice highlighted.</summary>
    void OpenStartChoices(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var choice in _vm.StartChoices)
        {
            var current = choice == _vm.StartChoice;
            // The ▶ starts it there and then, so a group is started in two clicks rather than picked and then started. Set
            // here rather than styled: the menu is a popup, outside the view's styles.
            // The button is the row's full height, so a pointer moving up or down the ▶s stays on them rather than slipping
            // onto a row and picking it; only its hover shows where it is.
            var now = new Button
            {
                Classes = { "startnow" },
                Content = new Border
                {
                    Child = new PathIcon { Width = 9, Height = 9, Data = PlayGlyph, Foreground = this.FindResource("AccentBrush") as Avalonia.Media.IBrush },
                    Padding = new Thickness(11, 4),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
                Background = Avalonia.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                // Out over the item's padding above and below, and right over the room it keeps for a submenu's arrow,
                // which these never have.
                Margin = new Thickness(16, -6, -18, -6),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ToolTip.SetTip(now, $"Start {(choice.Group is null ? "every app" : choice.Label)} now");
            now.Click += (_, e) =>
            {
                e.Handled = true;
                menu.Close();
                _vm?.StartChoiceNowCommand.Execute(choice);
            };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock
            {
                Text = choice.Label,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontWeight = current ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
            });
            Grid.SetColumn(now, 1);
            header.Children.Add(now);
            var item = new MenuItem { Header = header, Padding = new Thickness(20, 6, 6, 6) };
            if (current) item.Background = CurrentChoiceBrush;
            ToolTip.SetTip(item, choice.Apps);
            item.Click += (_, _) => _vm?.PickStartChoiceCommand.Execute(choice);
            menu.Items.Add(item);
        }
        menu.Open(StartChoiceButton);
    }

    static readonly Avalonia.Media.Geometry PlayGlyph = Avalonia.Media.Geometry.Parse("M6 3v18l15-9z");

    /// <summary>The nav's tint for the page that's showing, so "the one picked" looks the same everywhere.</summary>
    static readonly Avalonia.Media.IBrush CurrentChoiceBrush = Avalonia.Media.Brush.Parse("#2660A5FA");

    static AppListEntry? EntryAt(object? source)
        => (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext)
            .OfType<AppListEntry>().FirstOrDefault();

    static WorkspaceAppRow? AppAt(object? source) => EntryAt(source)?.Row;

    /// <summary>Ctrl (or Cmd) and Shift clicks mark rows as in a file list; handled before the row's button sees the
    /// press, so they don't also open the app's log.</summary>
    void OnAppPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !e.GetCurrentPoint(AppList).Properties.IsLeftButtonPressed || EntryAt(e.Source) is not { } entry) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _vm.MarkRange(entry);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) _vm.ToggleMark(entry.Row);
        else return;
        e.Handled = true;
    }

    void OnAppContext(object? sender, ContextRequestedEventArgs e)
    {
        if (AppAt(e.Source) is not { } app || e.Source is not Control at) return;
        ShowAppMenu(app, at, PlacementMode.Pointer);
        e.Handled = true;
    }

    void OpenAppMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button && AppAt(button) is { } app) ShowAppMenu(app, button, PlacementMode.BottomEdgeAlignedRight);
    }

    /// <summary>A marked row's menu is for every marked app; any other row's is for it alone, and lets go of the marks,
    /// as a right-click outside a file list's selection does.</summary>
    void ShowAppMenu(WorkspaceAppRow app, Control at, PlacementMode placement)
    {
        if (_vm is { HasMarks: true } && app.IsMarked && _vm.MarkedApps is { Count: > 1 } marked)
        {
            ShowMarksMenu(marked, at, placement);
            return;
        }
        _vm?.ClearMarks();
        _menuApp = app;
        _appStart.Header = app.IsAlive ? "Restart" : "Start";
        _appStop.IsVisible = app.IsAlive;
        _appPin.Header = app.IsPinned ? "Unpin" : "Pin";
        _appMenu.Items.Clear();
        _appMenu.Items.Add(_appStart);
        _appMenu.Items.Add(_appStop);
        _appMenu.Items.Add(new Separator());
        _appMenu.Items.Add(_appPin);
        // Its links too, so a page can be opened from the row without picking the app for its details first.
        var links = _vm?.LinksOf(app) ?? [];
        if (links.Count > 0) _appMenu.Items.Add(new Separator());
        foreach (var link in links) _appMenu.Items.Add(LinkItem(_appMenu, link.Label, link.Url));
        _appMenu.Placement = placement;
        _appMenu.Open(at);
    }

    void ShowMarksMenu(System.Collections.Generic.IReadOnlyList<WorkspaceAppRow> marked, Control at, PlacementMode placement)
    {
        var alive = marked.Count(a => a.IsAlive);
        var stopped = marked.Count - alive;
        // A mix says which part each item is for; all alike, it's simply that many apps.
        _marksStart.Header = alive == 0 ? $"Start {Apps(stopped)}" : $"Start {stopped} not running";
        _marksStart.IsVisible = stopped > 0;
        _marksRestart.Header = stopped == 0 ? $"Restart {Apps(alive)}" : $"Restart {alive} running";
        _marksRestart.IsVisible = alive > 0;
        _marksStop.Header = stopped == 0 ? $"Stop {Apps(alive)}" : $"Stop {alive} running";
        _marksStop.IsVisible = alive > 0;
        _marksPin.Header = marked.Any(a => !a.IsPinned) ? $"Pin {Apps(marked.Count)}" : $"Unpin {Apps(marked.Count)}";
        _marksMenu.Placement = placement;
        _marksMenu.Open(at);
    }

    static string Apps(int n) => n == 1 ? "1 app" : $"{n} apps";

    /// <summary>A <c>link</c> text, or an <c>opens</c> button, with its url in <see cref="Control.Tag"/>, under the pointer.</summary>
    static Control? LinkAt(object? source)
        => (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(c => (c is TextBlock && c.Classes.Contains("link")) || (c is Button && c.Classes.Contains("opens"))) is { Tag: string { Length: > 0 } } link
            ? link : null;

    /// <summary>A double-click on a ticking box only ticks it twice, so it doesn't also open the workspace.</summary>
    void OnOverviewRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
        if ((sender as Control)?.DataContext is OverviewRow row) _vm?.OpenOverviewRowCommand.Execute(row);
    }

    /// <summary>As a link in a browser: a click opens it, a middle-click opens it in a window of its own.</summary>
    void OnLinkReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (LinkAt(e.Source) is not { IsPointerOver: true } link) return;
        // A button's own click (or Enter on it) already opens it.
        if (e.InitialPressMouseButton == MouseButton.Left && link is Button) return;
        if (e.InitialPressMouseButton == MouseButton.Left) OpenLink((string)link.Tag!, newWindow: false);
        else if (e.InitialPressMouseButton == MouseButton.Middle) OpenLink((string)link.Tag!, newWindow: true);
        else return;
        e.Handled = true;
    }

    void OnLinkContext(object? sender, ContextRequestedEventArgs e)
    {
        if (LinkAt(e.Source) is not { } link) return;
        _menuUrl = (string)link.Tag!;
        _linkMenu.Open(link);
        e.Handled = true;
    }

    void OpenLink(string? url, bool newWindow)
    {
        if (url is null) return;
        if (newWindow) Browser.OpenInNewWindow(url);
        else Browser.Open(url);
        LinksButton.Flyout?.Hide();
    }

    /// <summary>Below about 760px the log would be squeezed to a sliver beside the details, so they go over it instead,
    /// folded until asked for; widening again puts them back as they were.</summary>
    void SetNarrow(bool narrow)
    {
        if (LogArea.Classes.Contains("narrow") == narrow) return;
        LogArea.Classes.Set("narrow", narrow);
        _vm?.FitDetails(narrow);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ProgressStarted -= ShowProgress;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Logs.ScrollToEnd -= ScrollLogToEnd;
            _vm.Logs.ScrollTo -= ScrollLogTo;
            _vm.Logs.Reveal -= RevealLogRow;
            _vm.Logs.PropertyChanged -= OnLogsChanged;
            _vm.Logs.TopRow = null;
        }
        _vm = DataContext as WorkspacesViewModel;
        if (_vm is not null)
        {
            _vm.ProgressStarted += ShowProgress;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Logs.ScrollToEnd += ScrollLogToEnd;
            _vm.Logs.ScrollTo += ScrollLogTo;
            _vm.Logs.Reveal += RevealLogRow;
            _vm.Logs.PropertyChanged += OnLogsChanged;
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
        if (e.PropertyName == nameof(WorkspacesViewModel.IsAddOpen) && _vm?.IsAddOpen == true)
            Dispatcher.UIThread.Post(() => NewBranchBox.Focus(), DispatcherPriority.Loaded);
    }

    void HookLogScroller()
    {
        if (_logScroller is not null) return;
        _logScroller = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_logScroller is null) return;
        _logScroller.ScrollChanged += OnLogScrolled;
        // Tunnelled, so the wheel pauses the log before the list scrolls, even at the very end where it can't move.
        LogList.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y > 0 && _logScroller.Extent.Height > _logScroller.Viewport.Height) _vm?.Logs.SetFollowing(false, TopRow());
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Any move up (the wheel, the scrollbar, Page Up) pauses the log where it is, and new lines then arrive below
    /// without moving it; moving back down to the end follows it again. The list's height is re-estimated as rows scroll
    /// into view, so a move is judged by which way the offset went, not by whether the height also changed. Moves sprig
    /// makes itself (following, putting a log back where it was left) are skipped.
    /// </summary>
    void OnLogScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || _logScroller is null || _scrolling) return;
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
            if (_logScroller is null || _vm?.Logs.Follow != true) return;
            Programmatically(() => _logScroller.ScrollToEnd());
        }, DispatcherPriority.Background);
    }

    /// <summary>Put <paramref name="row"/> at the top of the view, where it was when the log was left or rebuilt.</summary>
    void ScrollLogTo(LogRow row) => Place(row, 0);

    /// <summary>A third of the way down, so what came just before it shows too, and picked out.</summary>
    void RevealLogRow(LogRow row) => Place(row, 1 / 3.0, pick: true);

    void Place(LogRow row, double fromTop, bool pick = false) => Dispatcher.UIThread.Post(() =>
    {
        HookLogScroller();
        if (_logScroller is null) return;
        if (pick) LogList.SelectedItem = row;
        Programmatically(() => LogList.ScrollIntoView(row));
        Dispatcher.UIThread.Post(() =>
        {
            if (LogList.ContainerFromItem(row) is not { } container || container.TranslatePoint(new Point(0, 0), _logScroller) is not { } at) return;
            var y = _logScroller.Offset.Y + at.Y - _logScroller.Viewport.Height * fromTop;
            Programmatically(() => _logScroller.Offset = new Vector(_logScroller.Offset.X, Math.Max(0, y)));
        }, DispatcherPriority.Background);
    }, DispatcherPriority.Background);

    /// <summary>Wrapping or the time column changes every row's height: a following log stays at its end, a paused one
    /// on the line that was at the top.</summary>
    void OnLogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null || e.PropertyName is not (nameof(WorkspaceLogPane.Wrap) or nameof(WorkspaceLogPane.TimesShown))) return;
        if (_vm.Logs.Follow) ScrollLogToEnd();
        else if (TopRow() is { } top) ScrollLogTo(top);
    }

    /// <summary>Picking lines pauses the log, so they aren't carried off while being read or copied.</summary>
    void OnLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is { Logs.Follow: true } && LogList.SelectedItems is { Count: > 0 }) _vm.Logs.SetFollowing(false, TopRow());
    }

    void OnLogContext(object? sender, ContextRequestedEventArgs e)
    {
        _menuRow = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LogRow;
        if (_menuRow is not null && LogList.SelectedItems?.Contains(_menuRow) != true) LogList.SelectedItem = _menuRow;
        var count = LogList.SelectedItems?.Count ?? 0;
        _copyLines.Header = count == 1 ? "Copy line" : $"Copy {count:N0} lines";
        _copyLines.IsEnabled = count > 0;
        _allAt.Header = _menuRow is { Line.Timed: true } timed ? $"Show all apps around {timed.Time}" : "Show all apps around this line";
        _allAt.IsVisible = _menuRow is not null && _vm?.Logs.IsOne == true;
        _appAt.Header = $"Show this line in {_menuRow?.Label}'s own log";
        _appAt.IsVisible = _menuRow is not null && _vm?.Logs.IsAll == true;
    }

    async void CopySelectedLines()
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var lines = _vm.Logs.Lines;
        var rows = LogList.Selection.SelectedIndexes.Where(i => i >= 0 && i < lines.Count).Order().Select(i => lines[i]).ToArray();
        if (rows.Length > 0) await clipboard.SetTextAsync(_vm.Logs.CopyText(rows));
    }

    /// <summary>A plain press starts a drag that picks every line from there to wherever the pointer goes; Shift and
    /// Ctrl clicks are left to the list.</summary>
    void OnLogPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(LogList);
        _dragged = false;
        _dragFrom = point.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None ? IndexAt(point.Position.Y) : -1;
    }

    void OnLogDragged(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0 || _logScroller is null) return;
        var point = e.GetCurrentPoint(LogList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragFrom = -1;
            return;
        }
        // Past the top or bottom: the log moves that way, so a pick can run on beyond what's in view.
        var top = _logScroller.TranslatePoint(new Point(0, 0), LogList)?.Y ?? 0;
        var over = point.Position.Y < top ? point.Position.Y - top
            : point.Position.Y > top + _logScroller.Viewport.Height ? point.Position.Y - top - _logScroller.Viewport.Height : 0;
        if (over != 0) _logScroller.Offset = new Vector(_logScroller.Offset.X, Math.Max(0, _logScroller.Offset.Y + over));

        var to = IndexAt(point.Position.Y);
        if (to < 0 || (to == _dragFrom && !_dragged)) return;
        _dragged = true;
        LogList.Selection.BeginBatchUpdate();
        LogList.Selection.Clear();
        LogList.Selection.SelectRange(Math.Min(_dragFrom, to), Math.Max(_dragFrom, to));
        LogList.Selection.EndBatchUpdate();
    }

    void OnLogReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Handled after a drag, so the list doesn't take the release as a click that picks the one line under it.
        if (_dragged) e.Handled = true;
        _dragFrom = -1;
        _dragged = false;
    }

    /// <summary>The line at <paramref name="y"/> (in the list's coordinates), or the nearest one in view.</summary>
    int IndexAt(double y)
    {
        var rows = LogList.GetRealizedContainers()
            .Select(c => (Index: LogList.IndexFromContainer(c), Top: c.TranslatePoint(new Point(0, 0), LogList)?.Y, c.Bounds.Height))
            .Where(r => r.Index >= 0 && r.Top is not null)
            .OrderBy(r => r.Top)
            .ToArray();
        if (rows.Length == 0) return -1;
        return rows.FirstOrDefault(r => y < r.Top + r.Height, rows[^1]).Index;
    }

    void Programmatically(Action scroll)
    {
        _scrolling = true;
        scroll();
        Dispatcher.UIThread.Post(() => _scrolling = false, DispatcherPriority.Background);
    }

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

    /// <summary>Up and Down move through the apps, Ctrl+F searches the log, End jumps to its latest line, Esc lets go of
    /// marked apps. Typing in a box keeps its keys.</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is { HasSelection: true, OnGraphTab: true, IsAddOpen: false } && e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            GraphSearchBox.Focus();
            GraphSearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (_vm is not { HasSelection: true, OnAppsTab: true } || _vm.IsAddOpen || _vm.ConfirmingRemove || _vm.ConfirmingTakeOver) return;
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            LogSearch.Focus();
            LogSearch.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Source is TextBox) return;
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && e.Source is Visual source
            && (source == LogList || LogList.IsVisualAncestorOf(source)) && LogList.SelectedItems is { Count: > 0 })
        {
            CopySelectedLines();
            e.Handled = true;
            return;
        }
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
            case Key.Escape when _vm.HasMarks:
                _vm.ClearMarks();
                e.Handled = true;
                break;
        }
    }

    void ToggleProblems(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Logs.ProblemsOnly = !_vm.Logs.ProblemsOnly;
    }

    void ShowProgress(OperationProgressViewModel progress)
    {
        var window = new OperationProgressWindow { DataContext = progress };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
}
