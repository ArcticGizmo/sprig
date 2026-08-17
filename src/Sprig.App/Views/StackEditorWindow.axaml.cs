using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

/// <summary>
/// The stack builder in its own resizable window (was an in-page overlay). DataContext is the shared
/// <see cref="StacksViewModel"/> so every binding is unchanged; the wiring canvas fills the window and
/// grows with it. A coach layer over the content lets a guided-tour step still spotlight the canvas and
/// the Create button — wired by the opener via <see cref="AttachCoach"/>.
/// </summary>
public partial class StackEditorWindow : Window
{
    /// <summary>
    /// The most recently opened editor window. The headless renderer captures it because the builder now
    /// lives here rather than inside the main window; the app only ever has one open at a time.
    /// </summary>
    internal static StackEditorWindow? Current { get; private set; }

    // Ports-rail drag-to-reorder state: the row whose grip is being dragged, plus a small jitter guard so
    // a click on the handle doesn't count as a drag.
    StackPortRow? _dragPort;
    bool _portDragMoved;
    Point _portPressPos;
    StacksViewModel? _vm;

    public StackEditorWindow()
    {
        InitializeComponent();
        // Block DataContext inheritance so the coach layer stays inert (e.g. static docs captures) until a
        // real run is attached — otherwise it would inherit the window's StacksViewModel and bind nothing.
        Coachmarks.DataContext = null;
        Current = this;

        // The ports rail reorders by dragging a row's grip handle; wire the pointer gestures here so the
        // per-row template stays declarative. A press starts a drag only on the grip, and it reorders live.
        PortsList.AddHandler(PointerPressedEvent, OnPortsPointerPressed, RoutingStrategies.Bubble);
        PortsList.AddHandler(PointerMovedEvent, OnPortsPointerMoved, RoutingStrategies.Bubble);
        PortsList.AddHandler(PointerReleasedEvent, OnPortsPointerReleased, RoutingStrategies.Bubble);
    }

    /// <summary>Point the editor's coach layer at this window's content, bound to the shared coach run.</summary>
    public void AttachCoach(CoachViewModel coach)
    {
        Coachmarks.Secondary = true;   // the main window's layer stays the primary that owns the run
        Coachmarks.DataContext = coach;
        Coachmarks.AnchorRoot = EditorRoot;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Current == this) Current = null;
        base.OnClosed(e);
    }

    // Track the shared view model so we can react to the port editor opening (below).
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as StacksViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        base.OnDataContextChanged(e);
    }

    // Focus + select the rename box each time the port editor opens, so Enter-to-save works without a
    // click first. Posted so it runs after the scrim becomes visible and the box is laid out.
    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StacksViewModel.PortEditor) && _vm?.PortEditor is not null)
            Dispatcher.UIThread.Post(() =>
            {
                PortEditorNameBox.Focus();
                PortEditorNameBox.SelectAll();
            });
    }

    // Escape closes the per-repo input editor if it's open, then the port editor, otherwise cancels the
    // whole edit (matching the old overlay). The cancel flows through the view model, which flips
    // IsCreating and lets the opener close this window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is StacksViewModel { RepoEditor: { } editor })
                editor.CloseCommand.Execute(null);
            else if (DataContext is StacksViewModel { PortEditor: not null } portVm)
                portVm.CancelPortEditorCommand.Execute(null);
            else
                (DataContext as StacksViewModel)?.CancelCreateCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // A click on the modal's dimmed backdrop (but not its inner panel) closes the input editor.
    void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, RepoEditorScrim)) return;
        (DataContext as StacksViewModel)?.RepoEditor?.CloseCommand.Execute(null);
        e.Handled = true;
    }

    // A click on the port editor's dimmed backdrop (but not its panel) cancels the rename.
    void OnPortScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PortEditorScrim)) return;
        (DataContext as StacksViewModel)?.CancelPortEditorCommand.Execute(null);
        e.Handled = true;
    }

    // -- ports rail: drag a row's grip to reorder ----------------------------
    // Order on the rail sets each port's host number, so a reorder is a real edit. Dragging moves the
    // grabbed row live over whichever row is under the cursor; MovePortTo reindexes + rebuilds.

    void OnPortsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PortsList).Properties.IsLeftButtonPressed) return;
        if (!IsInGrip(e.Source)) return;               // only the grip starts a drag; buttons still click
        _dragPort = RowOf(e.Source);
        if (_dragPort is null) return;
        _portPressPos = e.GetPosition(PortsList);
        _portDragMoved = false;
        e.Pointer.Capture(PortsList);
        e.Handled = true;
    }

    void OnPortsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPort is null || DataContext is not StacksViewModel vm) return;
        var pos = e.GetPosition(PortsList);
        var dx = pos.X - _portPressPos.X;
        var dy = pos.Y - _portPressPos.Y;
        if (!_portDragMoved && dx * dx + dy * dy < 16) return;   // ignore jitter until it's a real drag
        _portDragMoved = true;
        if (PortUnder(pos) is { } target) vm.MovePortTo(_dragPort, target);
    }

    void OnPortsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragPort is null) return;
        e.Pointer.Capture(null);
        _dragPort = null;
        e.Handled = true;
    }

    // True when the pressed element is within a row's drag-handle (class "port-grip").
    static bool IsInGrip(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Control c && c.Classes.Contains("port-grip")) return true;
        return false;
    }

    // The port row an element belongs to — walk up to the first visual carrying a StackPortRow context.
    static StackPortRow? RowOf(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Control c && c.DataContext is StackPortRow r) return r;
        return null;
    }

    // The port row under a point in the list (used to pick the drop target while dragging).
    StackPortRow? PortUnder(Point p) => RowOf(PortsList.InputHitTest(p));
}
