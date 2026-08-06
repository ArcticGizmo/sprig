using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using Sprig.Core.Stacks;

namespace Sprig.App.Controls;

/// <summary>
/// The patchbay: a read-only diagram of a stack's wiring. Every port is a node down a rail on the
/// left; every repo is stacked on the right with an input pin per declared input. A cable runs from
/// each port across to the inputs that consume it — so a port feeding two repos fans out into two
/// cables you can see at a glance. Shared ports are highlighted and transform cables use the
/// transform colour; hovering a port dims the other cables and shows a tooltip naming what consumes
/// it. Layout is deterministic and derived from <see cref="WiringGraph"/>; this control only draws.
/// </summary>
public sealed class WiringCanvas : Control, ICustomHitTest, Coach.IAnchorSource
{
    // The whole board is interactive (it's drawn, not templated, so give it a solid hit area).
    public bool HitTest(Point point) => true;

    /// <summary>
    /// Publish a drawn element's geometry so a coachmark can point at it. Nothing new is computed — these
    /// are the same rects <see cref="BuildLayout"/> already produces for hit-testing and dragging, so the
    /// coach can never disagree with what the user sees or can click.
    ///
    /// Meaningful only after a layout pass (<see cref="MeasureOverride"/> runs <c>BuildLayout</c>). Before
    /// that, or for anything not currently drawn, this returns false and the coach skips the step.
    /// </summary>
    public bool TryGetAnchor(string anchorId, out Rect bounds)
    {
        bounds = default;

        if (anchorId == Coach.Anchors.StackAutoWire)
        {
            bounds = _autoWireRect;
            return bounds is { Width: > 0, Height: > 0 };
        }

        if (Suffix(anchorId, "stack.port:") is { } port)
            return _portRects.TryGetValue(port, out bounds);

        if (Suffix(anchorId, "stack.node:") is { } repo)
        {
            var match = _repoBoxes.FirstOrDefault(b => b.Repo == repo);
            if (match.Repo is null) return false;
            bounds = match.Rect;
            return true;
        }

        if (Suffix(anchorId, "stack.pin:") is { } pin)
        {
            var slash = pin.IndexOf('/');
            return slash > 0 && _pinHit.TryGetValue((pin[..slash], pin[(slash + 1)..]), out bounds);
        }

        return false;

        static string? Suffix(string id, string prefix)
            => id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : null;
    }

    public static readonly StyledProperty<WiringGraph?> GraphProperty =
        AvaloniaProperty.Register<WiringCanvas, WiringGraph?>(nameof(Graph));

    /// <summary>When true, input pins can be dragged onto ports to wire them (and off to unbind).</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<WiringCanvas, bool>(nameof(IsEditable));

    /// <summary>Invoked with a <see cref="WireRequest"/> when a pin is dropped on a port.</summary>
    public static readonly StyledProperty<ICommand?> WireCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(WireCommand));

    /// <summary>Invoked with a <see cref="PinRef"/> when a bound pin is dropped on empty space (or "Unbind").</summary>
    public static readonly StyledProperty<ICommand?> UnwireCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(UnwireCommand));

    /// <summary>Invoked with a <see cref="PinRef"/> when the workspace source is dropped on an input.</summary>
    public static readonly StyledProperty<ICommand?> WireWorkspaceCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(WireWorkspaceCommand));

    /// <summary>Invoked with a <see cref="CreatePortRequest"/> when the "create new…" slot is dropped on an input.</summary>
    public static readonly StyledProperty<ICommand?> CreatePortCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(CreatePortCommand));

    /// <summary>Invoked with a <see cref="SetExpressionRequest"/> from the inline expression editor.</summary>
    public static readonly StyledProperty<ICommand?> SetExpressionCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(SetExpressionCommand));

    /// <summary>The tokens the inline editor autosuggests (<c>workspace</c> + one per named port).</summary>
    public static readonly StyledProperty<System.Collections.IEnumerable?> VariablesProperty =
        AvaloniaProperty.Register<WiringCanvas, System.Collections.IEnumerable?>(nameof(Variables));

    /// <summary>Invoked with a <see cref="RenamePortRequest"/> from the port menu's Rename action.</summary>
    public static readonly StyledProperty<ICommand?> RenamePortCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(RenamePortCommand));

    /// <summary>Invoked with a port name (string) from the port menu's Remove action.</summary>
    public static readonly StyledProperty<ICommand?> RemovePortCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(RemovePortCommand));

    /// <summary>Invoked with a port name (string) when the "create new…" slot is clicked (no drag).</summary>
    public static readonly StyledProperty<ICommand?> AddPortCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(AddPortCommand));

    /// <summary>Invoked with an <see cref="AppendSourceRequest"/> when a source is dropped on a transform node.</summary>
    public static readonly StyledProperty<ICommand?> AppendSourceCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(AppendSourceCommand));

    /// <summary>Registered repos not yet in the stack — listed by the canvas "add repo" slot.</summary>
    public static readonly StyledProperty<System.Collections.IEnumerable?> AddableReposProperty =
        AvaloniaProperty.Register<WiringCanvas, System.Collections.IEnumerable?>(nameof(AddableRepos));

    /// <summary>Invoked with a repo name (string) from the canvas "add repo" slot.</summary>
    public static readonly StyledProperty<ICommand?> AddRepoCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(AddRepoCommand));

    /// <summary>Invoked with a repo name (string) when a repo's trash icon is confirmed.</summary>
    public static readonly StyledProperty<ICommand?> RemoveRepoCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(RemoveRepoCommand));

    /// <summary>Invoked (no argument) from the on-canvas Auto-wire button.</summary>
    public static readonly StyledProperty<ICommand?> AutoWireCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(AutoWireCommand));

    /// <summary>Invoked with a <see cref="ReorderRepoRequest"/> when a repo box is dragged to a new slot.</summary>
    public static readonly StyledProperty<ICommand?> ReorderRepoCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(ReorderRepoCommand));

    /// <summary>Invoked with a <see cref="ReorderPortRequest"/> when a port is dragged to a new rail slot.</summary>
    public static readonly StyledProperty<ICommand?> ReorderPortCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(ReorderPortCommand));

    public WiringGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }
    public ICommand? WireCommand { get => GetValue(WireCommandProperty); set => SetValue(WireCommandProperty, value); }
    public ICommand? UnwireCommand { get => GetValue(UnwireCommandProperty); set => SetValue(UnwireCommandProperty, value); }
    public ICommand? WireWorkspaceCommand { get => GetValue(WireWorkspaceCommandProperty); set => SetValue(WireWorkspaceCommandProperty, value); }
    public ICommand? CreatePortCommand { get => GetValue(CreatePortCommandProperty); set => SetValue(CreatePortCommandProperty, value); }
    public ICommand? SetExpressionCommand { get => GetValue(SetExpressionCommandProperty); set => SetValue(SetExpressionCommandProperty, value); }
    public System.Collections.IEnumerable? Variables { get => GetValue(VariablesProperty); set => SetValue(VariablesProperty, value); }
    public ICommand? RenamePortCommand { get => GetValue(RenamePortCommandProperty); set => SetValue(RenamePortCommandProperty, value); }
    public ICommand? RemovePortCommand { get => GetValue(RemovePortCommandProperty); set => SetValue(RemovePortCommandProperty, value); }
    public ICommand? AddPortCommand { get => GetValue(AddPortCommandProperty); set => SetValue(AddPortCommandProperty, value); }
    public ICommand? AppendSourceCommand { get => GetValue(AppendSourceCommandProperty); set => SetValue(AppendSourceCommandProperty, value); }
    public System.Collections.IEnumerable? AddableRepos { get => GetValue(AddableReposProperty); set => SetValue(AddableReposProperty, value); }
    public ICommand? AddRepoCommand { get => GetValue(AddRepoCommandProperty); set => SetValue(AddRepoCommandProperty, value); }
    public ICommand? RemoveRepoCommand { get => GetValue(RemoveRepoCommandProperty); set => SetValue(RemoveRepoCommandProperty, value); }
    public ICommand? AutoWireCommand { get => GetValue(AutoWireCommandProperty); set => SetValue(AutoWireCommandProperty, value); }
    public ICommand? ReorderRepoCommand { get => GetValue(ReorderRepoCommandProperty); set => SetValue(ReorderRepoCommandProperty, value); }
    public ICommand? ReorderPortCommand { get => GetValue(ReorderPortCommandProperty); set => SetValue(ReorderPortCommandProperty, value); }

    // Palette (mirrors App.axaml).
    static readonly IBrush Bg = Brush.Parse("#181820");
    static readonly IBrush Panel = Brush.Parse("#1F1F2A");
    static readonly IBrush PanelHead = Brush.Parse("#14141B");
    static readonly IBrush Fg = Brush.Parse("#E1E1EB");
    static readonly IBrush Title = Brush.Parse("#F5F5FA");
    static readonly IBrush Muted = Brush.Parse("#8C8CA0");
    static readonly IBrush Border = Brush.Parse("#2D2D3C");
    static readonly IBrush Wire = Brush.Parse("#60A5FA");
    static readonly IBrush Signal = Brush.Parse("#4ADE80");
    static readonly IBrush Xform = Brush.Parse("#FF5FB0");
    static readonly IBrush Ws = Brush.Parse("#A78BFA");        // the workspace source (a string producer)
    static readonly IBrush Danger = Brush.Parse("#F87171");
    static readonly IBrush TipBg = Brush.Parse("#0F0F16");

    const double BoardW = 820, NodeW = 260, HeadH = 30, RowH = 30, NodePad = 12;
    const double PortX = 24, PortW = 170, PortH = 28, PortGap = 60, RailTop = 24;
    const double RepoTop = 24, RepoGap = 20;
    const double XformW = 168, XformH = 26; // centre-column transform node

    // Sentinel source names on the rail. The workspace is a real built-in source; the phantom slot is
    // the "create new…" affordance you drag from to mint a port (wired in a later phase).
    public const string WorkspaceSource = "\0workspace";
    public const string CreatePortSlot = "\0create";

    readonly Typeface _mono = new("Consolas");
    readonly Dictionary<string, Rect> _portRects = new(StringComparer.Ordinal);
    readonly Dictionary<string, Point> _portAnchor = new(StringComparer.Ordinal); // right-edge outlet
    readonly Dictionary<(string Repo, string Input), (Point Pt, bool Bound, bool Problem)> _pins = new();
    readonly Dictionary<(string Repo, string Input), Rect> _pinHit = new();        // grab area (jack + label)
    readonly List<(string Repo, Rect Rect, WiringRepoNode Node)> _repoBoxes = new();
    // Centre-column transform nodes, keyed by the (repo, input) they shape, with the node's expression.
    readonly Dictionary<(string Repo, string Input), (Rect Rect, string Expression)> _xforms = new();
    // On-canvas chrome (editing only): the "add repo" slot, per-repo trash icons, the Auto-wire button.
    readonly Dictionary<string, Rect> _repoTrash = new(StringComparer.Ordinal);
    Rect _addRepoRect, _autoWireRect;
    string? _hoverTrash;
    // The exact graph the layout dictionaries were built from. Render draws THIS, not the live Graph,
    // so a compositor commit that lands between a Graph change and the re-measure can't look up a key
    // that isn't in the (still-stale) dictionaries. Rendering slightly-behind is fine; crashing isn't.
    WiringGraph? _laidOut;
    double _height = 300;
    string? _hoverPort;
    Point _hoverPos;

    // Drag-to-wire state. Two gestures: dragging a SOURCE (a port, the workspace, or the phantom
    // "create new…" slot) onto an input to wire it; and dragging a bound INPUT off onto empty space
    // to unbind it.
    string? _dragSource;                    // the rail slot being dragged from (port name / sentinel)
    (string Repo, string Input)? _dragPin;  // an input being dragged (to a source to wire, or off to unbind)
    (string Repo, string Input)? _dropPin;  // the input currently under a source drag (drop target)
    (string Repo, string Input)? _dropXform;// the transform node under a source drag (fan-in target)
    string? _dropSource;                    // the source under an input drag (reverse-wire drop target)
    (string Repo, string Input)? _hoverXform; // the transform node under the cursor
    Point _pressPos;
    Point _dragCursor;
    bool _dragMoved;

    // Line selection: click a cable to select its binding; small Delete / Transform actions appear.
    (string Repo, string Input)? _selectedInput;
    Rect _deleteBtn, _transformBtn; // action hit-boxes, valid while _selectedInput is set

    // Reorder drag: picking a repo box up/down (grabbed by its header) or a port up/down the rail
    // (grabbed by its grip) to change its position. Indices are into the graph's Repos / Ports lists.
    int _reorderRepoFrom = -1;
    int _reorderPortFrom = -1;
    int _reorderTo = -1; // live insertion index (0..count) while a reorder drag is in flight
    const double GripW = 16;

    static WiringCanvas()
    {
        AffectsMeasure<WiringCanvas>(GraphProperty);
        AffectsRender<WiringCanvas>(GraphProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        BuildLayout();
        return new Size(BoardW, _height);
    }

    void BuildLayout()
    {
        _portRects.Clear();
        _portAnchor.Clear();
        _pins.Clear();
        _pinHit.Clear();
        _repoBoxes.Clear();
        _xforms.Clear();
        _repoTrash.Clear();
        _addRepoRect = default;
        _autoWireRect = default;

        var g = Graph;
        _laidOut = g; // the dictionaries below correspond to this snapshot; Render uses it too
        if (g is null) { _height = 200; return; }

        // The left rail, top to bottom: the workspace source, the named ports, then (while editing)
        // the phantom "create new…" slot. All keyed into the same maps so hit-testing/anchoring is uniform.
        var slot = 0;

        // Workspace source — always draggable while editing; shown read-only only when something uses it.
        if (IsEditable || g.Workspace.Used)
        {
            var wsRect = new Rect(PortX, RailTop + slot * PortGap, PortW, PortH);
            _portRects[WorkspaceSource] = wsRect;
            _portAnchor[WorkspaceSource] = new Point(wsRect.Right, wsRect.Center.Y);
            slot++;
        }

        for (var i = 0; i < g.Ports.Count; i++)
        {
            var rect = new Rect(PortX, RailTop + slot * PortGap, PortW, PortH);
            _portRects[g.Ports[i].Name] = rect;
            _portAnchor[g.Ports[i].Name] = new Point(rect.Right, rect.Center.Y);
            slot++;
        }

        // Phantom "create new…" slot at the bottom (editing only).
        if (IsEditable)
        {
            var addRect = new Rect(PortX, RailTop + slot * PortGap, PortW, PortH);
            _portRects[CreatePortSlot] = addRect;
            _portAnchor[CreatePortSlot] = new Point(addRect.Right, addRect.Center.Y);
            slot++;
        }

        var portsBottom = RailTop + Math.Max(1, slot) * PortGap + 12;

        // Repos: stacked on the right, pins on their left edge (facing the rail).
        var repoX = BoardW - 24 - NodeW;
        var y2 = RepoTop;
        foreach (var repo in g.Repos)
        {
            var h = HeadH + Math.Max(1, repo.Pins.Count) * RowH + NodePad;
            var rect = new Rect(repoX, y2, NodeW, h);
            _repoBoxes.Add((repo.Repo, rect, repo));
            if (IsEditable) _repoTrash[repo.Repo] = new Rect(rect.Right - 26, rect.Y + 7, 18, 18);

            for (var p = 0; p < repo.Pins.Count; p++)
            {
                var pin = repo.Pins[p];
                var py = y2 + HeadH + p * RowH + RowH / 2;
                var problem = pin.Kind == BindingKind.Unbound || pin.UndeclaredPort;
                _pins[(repo.Repo, pin.Input)] = (new Point(repoX, py), pin.HasPort, problem);
                // Grab area covers the jack and its label, so the whole input row is a drag handle.
                _pinHit[(repo.Repo, pin.Input)] = new Rect(repoX - 12, py - RowH / 2, 172, RowH);
            }

            y2 += h + RepoGap;
        }

        // "Add repo…" slot: a phantom node at the bottom of the repo column (editing only).
        if (IsEditable)
        {
            _addRepoRect = new Rect(repoX, y2, NodeW, HeadH);
            y2 += HeadH + RepoGap;
        }

        // Transform nodes: one per shaped input, in the centre column, aligned to its input row.
        var centreX = ((PortX + PortW) + (BoardW - 24 - NodeW)) / 2 - XformW / 2;
        foreach (var tn in g.TransformNodes)
        {
            if (!_pins.TryGetValue((tn.Repo, tn.Input), out var pv)) continue;
            var rect = new Rect(centreX, pv.Pt.Y - XformH / 2, XformW, XformH);
            _xforms[(tn.Repo, tn.Input)] = (rect, tn.Expression);
        }

        // Auto-wire button: on-canvas, at the top of the centre (transform) column.
        if (IsEditable)
            _autoWireRect = new Rect(centreX + (XformW - 116) / 2, 2, 116, 24);

        _height = Math.Max(portsBottom, y2) + 12;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(0, 0, BoardW, _height));
        var g = _laidOut; // draw the snapshot the layout dictionaries were built from
        if (g is null) return;

        // Cables first, so nodes sit on top. A source cable ends at the transform node when the input
        // has one (port → node), otherwise straight at the input pin (port → input).
        foreach (var e in g.Edges)
        {
            if (!_portAnchor.TryGetValue(e.Port, out var start)) continue;
            var end = SourceEndFor((e.Repo, e.Input));
            if (end is not { } to) continue;

            var dim = _dragPin is null && _dragSource is null && _hoverPort is not null && _hoverPort != e.Port;
            Pen pen = dim
                ? new Pen(new SolidColorBrush(((ISolidColorBrush)Wire).Color, 0.12), 1)
                : new Pen(Wire, e.Shared ? 3 : 2.2) { LineCap = PenLineCap.Round };
            ctx.DrawGeometry(null, pen, Cable(start, to));
        }

        // Workspace cables: from the workspace source to every input that references it (via its node).
        if (_portAnchor.TryGetValue(WorkspaceSource, out var wsAnchor))
        {
            foreach (var repo in g.Repos)
                foreach (var pinModel in repo.Pins)
                {
                    if (!pinModel.UsesWorkspace) continue;
                    if (SourceEndFor((repo.Repo, pinModel.Input)) is not { } to) continue;

                    var dim = _dragPin is null && _dragSource is null && _hoverPort is not null && _hoverPort != WorkspaceSource;
                    Pen pen = dim
                        ? new Pen(new SolidColorBrush(((ISolidColorBrush)Ws).Color, 0.12), 1)
                        : new Pen(Ws, g.Workspace.Shared ? 3 : 2.2) { LineCap = PenLineCap.Round };
                    ctx.DrawGeometry(null, pen, Cable(wsAnchor, to));
                }
        }

        // Transform node → input segments (transform-coloured), under the node boxes.
        foreach (var (key, xf) in _xforms)
        {
            if (!_pins.TryGetValue(key, out var pin)) continue;
            var outPt = new Point(xf.Rect.Right, xf.Rect.Center.Y);
            ctx.DrawGeometry(null, new Pen(Xform, 2.4) { LineCap = PenLineCap.Round }, Cable(outPt, pin.Pt));
        }

        // Port rail (left).
        foreach (var port in g.Ports)
        {
            if (!_portRects.TryGetValue(port.Name, out var rect)) continue;
            _portAnchor.TryGetValue(port.Name, out var anchor);
            var faded = _dragPin is null && _hoverPort is not null && _hoverPort != port.Name;
            var dropTarget = _dragPin is not null && _hoverPort == port.Name;
            var border = port.Shared ? Wire : (port.Used ? Signal : Border);
            var fill = port.Used ? Brush.Parse("#152417") : PanelHead;
            var pen = new Pen(border, port.Shared ? 2 : 1);
            using (ctx.PushOpacity(faded ? 0.35 : 1.0))
            {
                ctx.DrawRectangle(fill, pen, rect, 8, 8);
                if (dropTarget) ctx.DrawRectangle(null, new Pen(Wire, 3), rect.Inflate(3), 10, 10);
                DrawText(ctx, port.Name, rect, port.Used ? Signal : Muted, 12.5, center: true);
                if (IsEditable) DrawGrip(ctx, new Rect(rect.X, rect.Y, GripW, rect.Height), Muted);
                if (port.Shared)
                    DrawText(ctx, "SHARED ×" + port.ConsumerCount,
                        new Rect(rect.X, rect.Y - 15, rect.Width, 13), Wire, 9, center: true);
                // Outlet dot on the right edge.
                ctx.DrawEllipse(border, null, anchor, 3.5, 3.5);
            }
        }

        // Workspace source node (top of the rail).
        if (_portRects.TryGetValue(WorkspaceSource, out var wsRect))
        {
            _portAnchor.TryGetValue(WorkspaceSource, out var anchor);
            var faded = _dragPin is null && _hoverPort is not null && _hoverPort != WorkspaceSource;
            var used = g.Workspace.Used;
            using (ctx.PushOpacity(faded ? 0.35 : 1.0))
            {
                ctx.DrawRectangle(Brush.Parse("#1C1830"), new Pen(Ws, used ? 2 : 1), wsRect, 8, 8);
                DrawText(ctx, "workspace", wsRect, Ws, 12.5, center: true);
                if (g.Workspace.Shared)
                    DrawText(ctx, "×" + g.Workspace.ConsumerCount,
                        new Rect(wsRect.X, wsRect.Y - 15, wsRect.Width, 13), Ws, 9, center: true);
                ctx.DrawEllipse(Ws, null, anchor, 3.5, 3.5);
            }
        }

        // Phantom "create new…" slot (bottom of the rail, editing only).
        if (_portRects.TryGetValue(CreatePortSlot, out var addRect))
        {
            var hot = _hoverPort == CreatePortSlot;
            var pen = new Pen(hot ? Wire : Muted, hot ? 2 : 1) { DashStyle = new DashStyle([3, 3], 0) };
            ctx.DrawRectangle(PanelHead, pen, addRect, 8, 8);
            DrawText(ctx, "+ create new…", addRect, hot ? Fg : Muted, 12, center: true);
        }

        // Repo nodes (right) with pins on their left edge.
        foreach (var (repoName, node, repo) in _repoBoxes)
        {
            ctx.DrawRectangle(Panel, new Pen(Border, 1.5), node, 12, 12);
            ctx.DrawRectangle(PanelHead, null, new Rect(node.X, node.Y, NodeW, HeadH), 12, 12);
            var repoTextX = IsEditable ? node.X + 26 : node.X + 14;
            if (IsEditable) DrawGrip(ctx, new Rect(node.X + 8, node.Y, GripW, HeadH), Muted);
            DrawText(ctx, repoName, new Rect(repoTextX, node.Y, node.Right - 20 - repoTextX, HeadH), Title, 13, vcenter: true);

            // Trash icon (top-right of the header) to remove the repo from the stack.
            if (_repoTrash.TryGetValue(repoName, out var trash))
                DrawTrash(ctx, trash, _hoverTrash == repoName ? Danger : Muted);

            foreach (var pin in repo.Pins)
            {
                if (!_pins.TryGetValue((repoName, pin.Input), out var pv)) continue;
                var (pt, bound, problem) = pv;
                DrawText(ctx, pin.Input, new Rect(node.X + 16, pt.Y - RowH / 2, NodeW - 26, RowH),
                    problem ? Danger : Fg, 12, vcenter: true);

                // A pure literal has no cable and no node, so show its value inline (right-aligned, muted).
                if (pin.IsLiteral && pin.Expression is { Length: > 0 } lit)
                {
                    var nameW = Ft(pin.Input, 12, Fg).Width;
                    var avail = NodeW - 26 - nameW - 16;
                    var text = Truncate(lit, Math.Max(24, avail), 11);
                    var ft = Ft(text, 11, Muted);
                    ctx.DrawText(ft, new Point(node.Right - 14 - ft.Width, pt.Y - ft.Height / 2));
                }

                var jackBrush = problem ? PanelHead : (bound ? Wire : PanelHead);
                var jackPen = new Pen(problem ? Danger : (bound ? Wire : Border), 2);
                ctx.DrawEllipse(jackBrush, jackPen, pt, 5.5, 5.5);
            }
        }

        // Transform node boxes (centre column), on top of the cables.
        foreach (var (key, xf) in _xforms)
        {
            var hot = _hoverXform == key || _dropXform == key;
            ctx.DrawRectangle(Brush.Parse("#241626"), new Pen(Xform, hot ? 2 : 1.4), xf.Rect, 6, 6);
            var text = Truncate(xf.Expression, xf.Rect.Width - 26, 11);
            DrawText(ctx, "ƒ", new Rect(xf.Rect.X + 8, xf.Rect.Y, 12, xf.Rect.Height), Xform, 11, vcenter: true);
            DrawText(ctx, text, new Rect(xf.Rect.X + 22, xf.Rect.Y, xf.Rect.Width - 26, xf.Rect.Height), Fg, 11, vcenter: true);
        }

        // "Add repo…" slot at the bottom of the repo column (dashed, like the source "create new…").
        if (_addRepoRect != default)
        {
            var hot = _hoverPort == null && _addRepoRect.Contains(_hoverPos);
            var pen = new Pen(hot ? Wire : Muted, hot ? 2 : 1) { DashStyle = new DashStyle([3, 3], 0) };
            ctx.DrawRectangle(PanelHead, pen, _addRepoRect, 10, 10);
            DrawText(ctx, "＋ add repo…", _addRepoRect, hot ? Fg : Muted, 12, center: true);
        }

        // On-canvas Auto-wire button, at the top of the transform column.
        if (_autoWireRect != default)
        {
            var hot = _autoWireRect.Contains(_hoverPos);
            ctx.DrawRectangle(hot ? PanelHead : Panel, new Pen(Signal, hot ? 2 : 1.3), _autoWireRect, 6, 6);
            DrawText(ctx, "⚡ Auto-wire", _autoWireRect, Signal, 12, center: true);
        }

        // Rubber-band while dragging a source (port / workspace / create-new) toward an input.
        if (_dragSource is { } ds && _dragMoved && _portAnchor.TryGetValue(ds, out var srcAnchor))
        {
            var colour = ds == WorkspaceSource ? Ws : Wire;
            var pen = new Pen(colour, 2.5) { DashStyle = new DashStyle([2, 3], 0), LineCap = PenLineCap.Round };
            ctx.DrawGeometry(null, pen, Cable(srcAnchor, _dragCursor));
            if (_dropPin is { } tgt && _pins.TryGetValue(tgt, out var tv))
                ctx.DrawEllipse(null, new Pen(colour, 3), tv.Pt, 9, 9); // drop-target ring
        }

        // Rubber-band while dragging an input — blue toward a source (will wire), red toward empty (unbind).
        if (_dragPin is { } dp && _dragMoved && _pins.TryGetValue(dp, out var pinPt))
        {
            var colour = _dropSource == WorkspaceSource ? Ws : (_dropSource is not null ? Wire : Danger);
            var pen = new Pen(colour, 2.5) { DashStyle = new DashStyle([2, 3], 0), LineCap = PenLineCap.Round };
            ctx.DrawGeometry(null, pen, Cable(pinPt.Pt, _dragCursor));
            if (_dropSource is { } s && _portRects.TryGetValue(s, out var sr))
                ctx.DrawRectangle(null, new Pen(colour, 3), sr.Inflate(3), 10, 10); // source drop-target ring
        }

        // Selected line: a ring on its input plus Delete / Transform quick actions beside it.
        if (_selectedInput is { } sel && _pins.TryGetValue(sel, out var sp))
        {
            ctx.DrawEllipse(null, new Pen(Wire, 2.5), sp.Pt, 9, 9);
            _transformBtn = new Rect(sp.Pt.X - 62, sp.Pt.Y - 11, 26, 22);
            _deleteBtn = new Rect(sp.Pt.X - 32, sp.Pt.Y - 11, 22, 22);
            ctx.DrawRectangle(Panel, new Pen(Xform, 1.4), _transformBtn, 5, 5);
            DrawText(ctx, "ƒ", _transformBtn, Xform, 12, center: true);
            ctx.DrawRectangle(Panel, new Pen(Danger, 1.4), _deleteBtn, 5, 5);
            DrawText(ctx, "✕", _deleteBtn, Danger, 12, center: true);
        }

        // Reorder in flight: a ring on the item being moved and a bright insertion line at the drop slot.
        if (_reorderRepoFrom >= 0 && _dragMoved && _repoBoxes.Count > 0)
        {
            if (_reorderRepoFrom < _repoBoxes.Count)
                ctx.DrawRectangle(null, new Pen(Wire, 2) { DashStyle = new DashStyle([3, 3], 0) },
                    _repoBoxes[_reorderRepoFrom].Rect.Inflate(2), 12, 12);
            var iy = _reorderTo < _repoBoxes.Count
                ? _repoBoxes[_reorderTo].Rect.Y - RepoGap / 2
                : _repoBoxes[^1].Rect.Bottom + RepoGap / 2;
            var rx = BoardW - 24 - NodeW;
            ctx.DrawLine(new Pen(Wire, 2.5) { LineCap = PenLineCap.Round }, new Point(rx, iy), new Point(rx + NodeW, iy));
        }
        if (_reorderPortFrom >= 0 && _dragMoved && g.Ports.Count > 0)
        {
            if (_reorderPortFrom < g.Ports.Count && _portRects.TryGetValue(g.Ports[_reorderPortFrom].Name, out var fr))
                ctx.DrawRectangle(null, new Pen(Wire, 2) { DashStyle = new DashStyle([3, 3], 0) }, fr.Inflate(2), 10, 10);
            double iy;
            if (_reorderTo < g.Ports.Count && _portRects.TryGetValue(g.Ports[_reorderTo].Name, out var tr))
                iy = tr.Y - (PortGap - PortH) / 2;
            else if (_portRects.TryGetValue(g.Ports[^1].Name, out var lr))
                iy = lr.Bottom + (PortGap - PortH) / 2;
            else iy = RailTop;
            ctx.DrawLine(new Pen(Wire, 2.5) { LineCap = PenLineCap.Round }, new Point(PortX, iy), new Point(PortX + PortW, iy));
        }

        if (_hoverPort is not null && !IsSentinel(_hoverPort) && _dragPin is null && _dragSource is null
            && _reorderRepoFrom < 0 && _reorderPortFrom < 0)
            DrawTooltip(ctx, g, _hoverPort);
    }

    /// <summary>The rail's non-port slots (workspace source, phantom create) that aren't real ports.</summary>
    static bool IsSentinel(string name) => name is WorkspaceSource or CreatePortSlot;

    // -- hover tooltip: what consumes this port ------------------------------

    void DrawTooltip(DrawingContext ctx, WiringGraph g, string port)
    {
        var consumers = g.Edges.Where(e => e.Port == port).ToList();
        var header = consumers.Count == 0
            ? $"{port} — not used yet"
            : $"{port} — used by {consumers.Count}";

        var lines = consumers.Select(c => ($"{c.Repo} · {c.Input}", c.Transform)).ToList();

        var headerFt = Ft(header, 11.5, Title);
        var lineFts = lines.Select(l => Ft(l.Item1 + (l.Item2 ? "   (transform)" : ""), 12, l.Item2 ? Xform : Fg)).ToList();

        const double padX = 12, padY = 10, lineH = 19;
        var width = Math.Max(headerFt.Width, lineFts.Count == 0 ? 0 : lineFts.Max(f => f.Width)) + padX * 2;
        var height = padY * 2 + headerFt.Height + 6 + lineFts.Count * lineH;

        // Position near the cursor, clamped to the board.
        var x = Math.Min(_hoverPos.X + 16, BoardW - width - 6);
        var y = Math.Min(_hoverPos.Y + 12, _height - height - 6);
        x = Math.Max(6, x);
        y = Math.Max(6, y);
        var box = new Rect(x, y, width, height);

        ctx.DrawRectangle(TipBg, new Pen(Wire, 1), box, 8, 8);
        ctx.DrawText(headerFt, new Point(x + padX, y + padY));
        var cy = y + padY + headerFt.Height + 6;
        foreach (var ft in lineFts)
        {
            ctx.DrawText(ft, new Point(x + padX, cy));
            cy += lineH;
        }
    }

    // -- geometry / text helpers ---------------------------------------------

    static StreamGeometry Cable(Point a, Point b)
    {
        var dx = Math.Max(40, Math.Abs(b.X - a.X) * 0.45);
        var dir = b.X >= a.X ? 1 : -1; // handles either direction (port→pin and the drag rubber-band)
        var c1 = new Point(a.X + dx * dir, a.Y);
        var c2 = new Point(b.X - dx * dir, b.Y);
        var geo = new StreamGeometry();
        using var c = geo.Open();
        c.BeginFigure(a, false);
        c.CubicBezierTo(c1, c2, b);
        c.EndFigure(false);
        return geo;
    }

    FormattedText Ft(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, size, brush);

    void DrawText(DrawingContext ctx, string text, Rect within, IBrush brush, double size,
        bool center = false, bool vcenter = false)
    {
        var ft = Ft(text, size, brush);
        var x = center ? within.X + (within.Width - ft.Width) / 2 : within.X;
        var y = (center || vcenter) ? within.Y + (within.Height - ft.Height) / 2 : within.Y;
        ctx.DrawText(ft, new Point(x, y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (IsEditable && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var pos = e.GetPosition(this);

            // 1. The selected line's quick actions win first.
            if (_selectedInput is { } sel && _deleteBtn.Contains(pos))
            {
                UnwireCommand?.Execute(new PinRef(sel.Repo, sel.Input));
                _selectedInput = null;
                e.Handled = true; InvalidateVisual();
            }
            else if (_selectedInput is { } selt && _transformBtn.Contains(pos))
            {
                OpenExpressionEditor(selt.Repo, selt.Input, ExpressionOf(selt)); // edit this line's expression
                e.Handled = true;
            }
            // 2. On-canvas chrome: a repo's trash icon, the "add repo…" slot, the Auto-wire button.
            else if (TrashAt(pos) is { } trashRepo)
            {
                _selectedInput = null;
                OpenRemoveRepoConfirm(trashRepo);
                e.Handled = true;
            }
            else if (_addRepoRect != default && _addRepoRect.Contains(pos))
            {
                _selectedInput = null;
                OpenAddRepoMenu();
                e.Handled = true;
            }
            else if (_autoWireRect != default && _autoWireRect.Contains(pos))
            {
                _selectedInput = null;
                AutoWireCommand?.Execute(null);
                e.Handled = true;
            }
            // 3. A click on a transform node opens its expression editor (it isn't a drag source).
            else if (XformAt(pos) is { } xkey && _xforms.TryGetValue(xkey, out var xf))
            {
                _selectedInput = null;
                OpenExpressionEditor(xkey.Repo, xkey.Input, xf.Expression);
                e.Handled = true;
            }
            // A press on a repo header (not its trash) begins a reorder drag for that repo box.
            else if (RepoHeaderAt(pos) is var repoIdx && repoIdx >= 0)
            {
                _selectedInput = null;
                _reorderRepoFrom = repoIdx;
                _reorderTo = repoIdx;
                _pressPos = _dragCursor = pos;
                _dragMoved = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
            }
            // A press on a port's grip begins a reorder drag for that port (the rest of the port wires).
            else if (PortGripAt(pos) is var portIdx && portIdx >= 0)
            {
                _selectedInput = null;
                _reorderPortFrom = portIdx;
                _reorderTo = portIdx;
                _pressPos = _dragCursor = pos;
                _dragMoved = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
            }
            // 3. A press on a rail slot (port / workspace / create-new) begins a source→input wire drag.
            else if (PortAt(pos) is { } source)
            {
                _selectedInput = null;
                _dragSource = source;
                _pressPos = _dragCursor = pos;
                _dragMoved = false;
                _hoverPort = null;
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
            }
            // 4. A press on an input pin begins a drag — to a source to (re)wire it, or off to unbind.
            else if (PinAt(pos) is { } pin)
            {
                _selectedInput = null;
                _dragPin = pin;
                _pressPos = _dragCursor = pos;
                _dragMoved = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
            }
            // 5. A click on a cable selects that binding (Delete / Transform actions appear).
            else if (EdgeAt(pos) is { } edgeKey)
            {
                _selectedInput = edgeKey;
                e.Handled = true;
                InvalidateVisual();
            }
            // 6. A click on empty space clears the selection.
            else if (_selectedInput is not null)
            {
                _selectedInput = null;
                InvalidateVisual();
            }
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_reorderRepoFrom >= 0)
        {
            _dragCursor = pos;
            if (!_dragMoved && Distance(_pressPos, pos) > 4) _dragMoved = true;
            _reorderTo = RepoInsertIndex(pos.Y);
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }

        if (_reorderPortFrom >= 0)
        {
            _dragCursor = pos;
            if (!_dragMoved && Distance(_pressPos, pos) > 4) _dragMoved = true;
            _reorderTo = PortInsertIndex(pos.Y);
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }

        if (_dragSource is not null)
        {
            _dragCursor = pos;
            if (!_dragMoved && Distance(_pressPos, pos) > 4) _dragMoved = true;
            _dropXform = _dragSource == CreatePortSlot ? null : XformAt(pos); // fan into a node…
            _dropPin = _dropXform is null ? PinAt(pos) : null;                // …or wire an input
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }

        if (_dragPin is not null)
        {
            _dragCursor = pos;
            if (!_dragMoved && Distance(_pressPos, pos) > 4) _dragMoved = true;
            _dropSource = PortAt(pos); // dropping on a source (re)wires it; off onto empty → unbind
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }

        var hit = PortAt(pos);
        var xhit = XformAt(pos);
        var trash = TrashAt(pos);
        var overChrome = _addRepoRect.Contains(pos) || _autoWireRect.Contains(pos);
        var changed = hit != _hoverPort || xhit != _hoverXform || trash != _hoverTrash;
        _hoverPort = hit;
        _hoverXform = xhit;
        _hoverTrash = trash;
        _hoverPos = pos;
        // Redraw on enter/leave, over a port (tooltip follows the cursor), or over hoverable chrome.
        if (changed || hit is not null || overChrome) InvalidateVisual();
        base.OnPointerMoved(e);
    }

    string? TrashAt(Point p)
    {
        foreach (var kv in _repoTrash)
            if (kv.Value.Inflate(3).Contains(p)) return kv.Key;
        return null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_reorderRepoFrom >= 0)
        {
            e.Pointer.Capture(null);
            var from = _reorderRepoFrom;
            var insert = _reorderTo;
            _reorderRepoFrom = -1;
            _reorderTo = -1;
            if (_dragMoved)
            {
                var to = insert > from ? insert - 1 : insert; // removal shifts everything after `from` down one
                if (to != from && to >= 0) ReorderRepoCommand?.Execute(new ReorderRepoRequest(from, to));
            }
            _dragMoved = false;
            e.Handled = true;
            InvalidateVisual();
        }
        else if (_reorderPortFrom >= 0)
        {
            e.Pointer.Capture(null);
            var from = _reorderPortFrom;
            var insert = _reorderTo;
            _reorderPortFrom = -1;
            _reorderTo = -1;
            if (_dragMoved)
            {
                var to = insert > from ? insert - 1 : insert;
                if (to != from && to >= 0) ReorderPortCommand?.Execute(new ReorderPortRequest(from, to));
            }
            // A grip click with no drag still offers the port's rename / remove menu.
            else if (_laidOut is { } lg && from < lg.Ports.Count)
                OpenPortMenu(lg.Ports[from].Name);
            _dragMoved = false;
            e.Handled = true;
            InvalidateVisual();
        }
        else if (_dragSource is { } source)
        {
            e.Pointer.Capture(null);
            var target = _dropPin;
            var xtarget = _dropXform;
            _dragSource = null;
            _dropPin = null;
            _dropXform = null;

            if (_dragMoved && xtarget is { } xf && source != CreatePortSlot)
            {
                // Fan a second source into an existing transform node.
                var token = source == WorkspaceSource ? "${sprig.workspace}" : $"${{sprig.ports.{source}}}";
                AppendSourceCommand?.Execute(new AppendSourceRequest(xf.Repo, xf.Input, token));
            }
            else if (_dragMoved && target is { } t)
            {
                if (source == CreatePortSlot)
                    PromptCreatePort(t.Repo, t.Input);       // name it, then create + wire
                else if (source == WorkspaceSource)
                    WireWorkspaceCommand?.Execute(new PinRef(t.Repo, t.Input));
                else
                    WireCommand?.Execute(new WireRequest(t.Repo, t.Input, source)); // a real port (replaces)
            }
            else if (!_dragMoved)
            {
                // A click with no drag: manage the slot itself.
                if (source == CreatePortSlot)
                    PromptAddPort();                          // name a new port (no wiring yet)
                else if (source != WorkspaceSource)
                    OpenPortMenu(source);                     // rename / remove a real port
            }

            _dragMoved = false;
            e.Handled = true;
            InvalidateVisual();
        }
        else if (_dragPin is { } pin)
        {
            e.Pointer.Capture(null);
            var src = _dropSource;
            _dragPin = null;
            _dropSource = null;

            if (_dragMoved)
            {
                // Dragging the input to a source (re)wires it — replacing any current binding.
                if (src == CreatePortSlot)
                    PromptCreatePort(pin.Repo, pin.Input);              // quick-add a new port from the repo side
                else if (src == WorkspaceSource)
                    WireWorkspaceCommand?.Execute(new PinRef(pin.Repo, pin.Input));
                else if (src is not null)
                    WireCommand?.Execute(new WireRequest(pin.Repo, pin.Input, src));
                else if (IsBound(pin))
                    UnwireCommand?.Execute(new PinRef(pin.Repo, pin.Input)); // dragged off onto empty → unbind
            }
            else if (IsBound(pin))
            {
                OpenPinMenu(pin); // a click on a bound input → transform / unbind / edit menu
            }
            else
            {
                OpenExpressionEditor(pin.Repo, pin.Input, ExpressionOf(pin)); // click an empty input → type a value
            }

            _dragMoved = false;
            e.Handled = true;
            InvalidateVisual();
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverPort is not null && _dragPin is null && _dragSource is null) { _hoverPort = null; InvalidateVisual(); }
        base.OnPointerExited(e);
    }

    /// <summary>Pop a small text box to name the new port, then raise <see cref="CreatePortCommand"/>.</summary>
    void PromptCreatePort(string repo, string input)
    {
        var box = new TextBox { PlaceholderText = "new port name, e.g. api_port", Width = 220, FontSize = 12 };
        var flyout = new Flyout { Content = box };

        void Commit()
        {
            var name = box.Text?.Trim() ?? "";
            flyout.Hide();
            if (name.Length > 0) CreatePortCommand?.Execute(new CreatePortRequest(repo, input, name));
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Commit(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; } // cancel aborts the line
        };

        flyout.ShowAt(this, showAtPointer: true);
        box.Focus();
    }

    /// <summary>Pop a text box to name a brand-new port (clicking the "create new…" slot without dragging).</summary>
    void PromptAddPort()
    {
        var box = new TextBox { PlaceholderText = "new port name, e.g. api_port", Width = 220, FontSize = 12 };
        var flyout = new Flyout { Content = box };

        void Commit()
        {
            var name = box.Text?.Trim() ?? "";
            flyout.Hide();
            if (name.Length > 0) AddPortCommand?.Execute(name);
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Commit(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };
        flyout.ShowAt(this, showAtPointer: true);
        box.Focus();
    }

    /// <summary>The rename / remove menu for a real stack port (a click on the port with no drag).</summary>
    void OpenPortMenu(string port)
    {
        var flyout = new MenuFlyout();
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptRenamePort(port);
        flyout.Items.Add(rename);
        var remove = new MenuItem { Header = "Remove port" };
        remove.Click += (_, _) => RemovePortCommand?.Execute(port);
        flyout.Items.Add(remove);
        flyout.ShowAt(this, showAtPointer: true);
    }

    /// <summary>The "add repo…" slot's menu: one item per registered repo not yet in the stack.</summary>
    void OpenAddRepoMenu()
    {
        var flyout = new MenuFlyout();
        var any = false;
        if (AddableRepos is not null)
            foreach (var obj in AddableRepos)
            {
                if (obj?.ToString() is not { Length: > 0 } name) continue;
                any = true;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => AddRepoCommand?.Execute(name);
                flyout.Items.Add(item);
            }
        if (!any) flyout.Items.Add(new MenuItem { Header = "No more repos to add", IsEnabled = false });
        flyout.ShowAt(this, showAtPointer: true);
    }

    /// <summary>Confirm before removing a repo from the stack (the trash icon).</summary>
    void OpenRemoveRepoConfirm(string repo)
    {
        var msg = new TextBlock
        {
            Text = $"Remove '{repo}' from this stack? Its inputs and their wiring are dropped.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 240, Margin = new Thickness(0, 0, 0, 4),
        };
        var remove = new Button { Content = "Remove", Foreground = Danger };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { remove, cancel },
        };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { msg, buttons } } };
        remove.Click += (_, _) => { flyout.Hide(); RemoveRepoCommand?.Execute(repo); };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(this, showAtPointer: true);
    }

    /// <summary>Draw a drag-handle grip (two columns of dots) centred in the left of <paramref name="zone"/>.</summary>
    static void DrawGrip(DrawingContext ctx, Rect zone, IBrush brush)
    {
        var cx = zone.X + 4;
        var cy = zone.Center.Y;
        for (var col = 0; col < 2; col++)
            for (var row = -1; row <= 1; row++)
                ctx.DrawEllipse(brush, null, new Point(cx + col * 4, cy + row * 4), 1, 1);
    }

    /// <summary>Draw a minimal trash-can glyph inside <paramref name="r"/>.</summary>
    static void DrawTrash(DrawingContext ctx, Rect r, IBrush brush)
    {
        var pen = new Pen(brush, 1.4) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        ctx.DrawLine(pen, new Point(x + 2, y + 4), new Point(x + w - 2, y + 4));            // lid
        ctx.DrawLine(pen, new Point(x + w * 0.36, y + 4), new Point(x + w * 0.36, y + 1.5)); // handle
        ctx.DrawLine(pen, new Point(x + w * 0.36, y + 1.5), new Point(x + w * 0.64, y + 1.5));
        ctx.DrawLine(pen, new Point(x + w * 0.64, y + 1.5), new Point(x + w * 0.64, y + 4));
        ctx.DrawRectangle(null, pen, new Rect(x + 3, y + 5, w - 6, h - 6), 1.5, 1.5);         // body
        ctx.DrawLine(pen, new Point(x + w * 0.42, y + 7), new Point(x + w * 0.42, y + h - 3)); // ribs
        ctx.DrawLine(pen, new Point(x + w * 0.58, y + 7), new Point(x + w * 0.58, y + h - 3));
    }

    /// <summary>Pop a text box prefilled with the port's name; commit renames it (and its bindings).</summary>
    void PromptRenamePort(string port)
    {
        var box = new TextBox { Text = port, Width = 220, FontSize = 12 };
        var flyout = new Flyout { Content = box };

        void Commit()
        {
            var name = box.Text?.Trim() ?? "";
            flyout.Hide();
            if (name.Length > 0 && name != port) RenamePortCommand?.Execute(new RenamePortRequest(port, name));
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Commit(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };
        flyout.ShowAt(this, showAtPointer: true);
        box.Focus();
        box.SelectAll();
    }

    /// <summary>
    /// Pop the inline expression editor for one input — the D1 "one expression per input" surface, a
    /// <see cref="SprigTokenBox"/> over the same tokens the form uses. Used for typing a literal or
    /// <c>${sprig.workspace}</c> on an empty input, and for editing a transform node's expression.
    /// </summary>
    void OpenExpressionEditor(string repo, string input, string current)
    {
        var box = new SprigTokenBox
        {
            Value = current,
            Variables = Variables,
            Watermark = "literal or ${sprig.ports.NAME}",
            Width = 300,
        };
        var hint = new TextBlock
        {
            Text = "Enter to save · Esc to cancel", FontSize = 10.5, Foreground = Muted,
        };
        var ok = new Button { Content = "Set", HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Width = 300, Children = { box, hint, ok } },
        };

        void Commit()
        {
            flyout.Hide();
            SetExpressionCommand?.Execute(new SetExpressionRequest(repo, input, box.Value?.Trim() ?? ""));
        }

        // Enter commits, Esc cancels. When the token box's completion popup is open it consumes these
        // first (accept/close the suggestion), so they only reach us once the popup is dismissed.
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Commit(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };

        ok.Click += (_, _) => Commit();
        flyout.ShowAt(this, showAtPointer: true);
        box.Focus();
    }

    /// <summary>The current expression for an input, from the graph snapshot the layout was built from.</summary>
    string ExpressionOf((string Repo, string Input) key) =>
        _laidOut?.Repos.FirstOrDefault(r => r.Repo == key.Repo)?
            .Pins.FirstOrDefault(p => p.Input == key.Input)?.Expression ?? "";

    (string Repo, string Input)? PinAt(Point p)
    {
        foreach (var kv in _pinHit)
            if (kv.Value.Contains(p)) return kv.Key;
        return null;
    }

    string? PortAt(Point p)
    {
        foreach (var kv in _portRects)
            if (kv.Value.Contains(p)) return kv.Key;
        return null;
    }

    (string Repo, string Input)? XformAt(Point p)
    {
        foreach (var kv in _xforms)
            if (kv.Value.Rect.Contains(p)) return kv.Key;
        return null;
    }

    /// <summary>Index into the graph's ports whose left-edge grip contains <paramref name="p"/>, or -1.</summary>
    int PortGripAt(Point p)
    {
        var g = _laidOut;
        if (g is null) return -1;
        for (var i = 0; i < g.Ports.Count; i++)
            if (_portRects.TryGetValue(g.Ports[i].Name, out var r) && new Rect(r.X, r.Y, GripW, r.Height).Contains(p))
                return i;
        return -1;
    }

    /// <summary>Index into <see cref="_repoBoxes"/> whose header contains <paramref name="p"/> (trash excluded), or -1.</summary>
    int RepoHeaderAt(Point p)
    {
        for (var i = 0; i < _repoBoxes.Count; i++)
        {
            var b = _repoBoxes[i];
            if (!new Rect(b.Rect.X, b.Rect.Y, NodeW, HeadH).Contains(p)) continue;
            if (_repoTrash.TryGetValue(b.Repo, out var t) && t.Inflate(3).Contains(p)) return -1; // trash wins
            return i;
        }
        return -1;
    }

    /// <summary>Insertion index (0..count) among repo boxes for a drop at vertical position <paramref name="y"/>.</summary>
    int RepoInsertIndex(double y)
    {
        for (var i = 0; i < _repoBoxes.Count; i++)
            if (y < _repoBoxes[i].Rect.Center.Y) return i;
        return _repoBoxes.Count;
    }

    /// <summary>Insertion index (0..count) among the graph's ports for a drop at vertical position <paramref name="y"/>.</summary>
    int PortInsertIndex(double y)
    {
        var g = _laidOut;
        if (g is null) return 0;
        for (var i = 0; i < g.Ports.Count; i++)
            if (_portRects.TryGetValue(g.Ports[i].Name, out var r) && y < r.Center.Y) return i;
        return g.Ports.Count;
    }

    /// <summary>The input whose cable runs nearest <paramref name="p"/> (within a small threshold), or null.</summary>
    (string Repo, string Input)? EdgeAt(Point p)
    {
        var g = _laidOut;
        if (g is null) return null;

        (string Repo, string Input)? best = null;
        var bestD = 7.0;
        void Test((string Repo, string Input) key, Point a, Point b)
        {
            var d = DistanceToCable(a, b, p);
            if (d < bestD) { bestD = d; best = key; }
        }

        foreach (var e in g.Edges)
            if (_portAnchor.TryGetValue(e.Port, out var a) && SourceEndFor((e.Repo, e.Input)) is { } end)
                Test((e.Repo, e.Input), a, end);

        if (_portAnchor.TryGetValue(WorkspaceSource, out var wa))
            foreach (var repo in g.Repos)
                foreach (var pinm in repo.Pins)
                    if (pinm.UsesWorkspace && SourceEndFor((repo.Repo, pinm.Input)) is { } end)
                        Test((repo.Repo, pinm.Input), wa, end);

        foreach (var (key, xf) in _xforms)
            if (_pins.TryGetValue(key, out var pin))
                Test(key, new Point(xf.Rect.Right, xf.Rect.Center.Y), pin.Pt);

        return best;
    }

    /// <summary>Min distance from <paramref name="p"/> to the bezier <see cref="Cable"/> would draw for a→b.</summary>
    static double DistanceToCable(Point a, Point b, Point p)
    {
        var dx = Math.Max(40, Math.Abs(b.X - a.X) * 0.45);
        var dir = b.X >= a.X ? 1 : -1;
        var c1 = new Point(a.X + dx * dir, a.Y);
        var c2 = new Point(b.X - dx * dir, b.Y);
        var min = double.MaxValue;
        const int steps = 24;
        for (var i = 0; i <= steps; i++)
        {
            var pt = Bezier(a, c1, c2, b, i / (double)steps);
            var d = Distance(pt, p);
            if (d < min) min = d;
        }
        return min;
    }

    static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
        return new Point(w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X,
                         w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
    }

    /// <summary>Where a source cable for this input ends: its transform node's left edge, or the pin.</summary>
    Point? SourceEndFor((string Repo, string Input) key) =>
        _xforms.TryGetValue(key, out var xf) ? new Point(xf.Rect.Left, xf.Rect.Center.Y)
        : _pins.TryGetValue(key, out var pin) ? pin.Pt
        : null;

    /// <summary>Cut <paramref name="s"/> with an ellipsis so it fits <paramref name="maxWidth"/> at the given size.</summary>
    string Truncate(string s, double maxWidth, double size)
    {
        if (Ft(s, size, Fg).Width <= maxWidth) return s;
        for (var len = s.Length - 1; len > 1; len--)
        {
            var t = s[..len] + "…";
            if (Ft(t, size, Fg).Width <= maxWidth) return t;
        }
        return "…";
    }

    bool IsBound((string Repo, string Input) pin) => _pins.TryGetValue(pin, out var v) && v.Bound;

    static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    void OpenPinMenu((string Repo, string Input) pin)
    {
        var flyout = new MenuFlyout();
        var edit = new MenuItem { Header = "Edit expression…" };
        edit.Click += (_, _) => OpenExpressionEditor(pin.Repo, pin.Input, ExpressionOf(pin));
        flyout.Items.Add(edit);
        var unbind = new MenuItem { Header = "Unbind" };
        unbind.Click += (_, _) => UnwireCommand?.Execute(new PinRef(pin.Repo, pin.Input));
        flyout.Items.Add(unbind);
        flyout.ShowAt(this, showAtPointer: true);
    }
}
