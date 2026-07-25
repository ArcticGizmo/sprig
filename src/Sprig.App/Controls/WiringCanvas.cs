using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
public sealed class WiringCanvas : Control, ICustomHitTest
{
    // The whole board is interactive (it's drawn, not templated, so give it a solid hit area).
    public bool HitTest(Point point) => true;

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

    /// <summary>Invoked with a <see cref="TransformRequest"/> when a transform is picked from a pin menu.</summary>
    public static readonly StyledProperty<ICommand?> TransformCommandProperty =
        AvaloniaProperty.Register<WiringCanvas, ICommand?>(nameof(TransformCommand));

    public WiringGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }
    public ICommand? WireCommand { get => GetValue(WireCommandProperty); set => SetValue(WireCommandProperty, value); }
    public ICommand? UnwireCommand { get => GetValue(UnwireCommandProperty); set => SetValue(UnwireCommandProperty, value); }
    public ICommand? TransformCommand { get => GetValue(TransformCommandProperty); set => SetValue(TransformCommandProperty, value); }

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
    static readonly IBrush Danger = Brush.Parse("#F87171");
    static readonly IBrush TipBg = Brush.Parse("#0F0F16");

    const double BoardW = 820, NodeW = 260, HeadH = 30, RowH = 30, NodePad = 12;
    const double PortX = 24, PortW = 170, PortH = 28, PortGap = 60, RailTop = 24;
    const double RepoTop = 24, RepoGap = 20;

    readonly Typeface _mono = new("Consolas");
    readonly Dictionary<string, Rect> _portRects = new(StringComparer.Ordinal);
    readonly Dictionary<string, Point> _portAnchor = new(StringComparer.Ordinal); // right-edge outlet
    readonly Dictionary<(string Repo, string Input), (Point Pt, bool Bound, bool Problem)> _pins = new();
    readonly Dictionary<(string Repo, string Input), Rect> _pinHit = new();        // grab area (jack + label)
    readonly List<(string Repo, Rect Rect, WiringRepoNode Node)> _repoBoxes = new();
    // The exact graph the layout dictionaries were built from. Render draws THIS, not the live Graph,
    // so a compositor commit that lands between a Graph change and the re-measure can't look up a key
    // that isn't in the (still-stale) dictionaries. Rendering slightly-behind is fine; crashing isn't.
    WiringGraph? _laidOut;
    double _height = 300;
    string? _hoverPort;
    Point _hoverPos;

    // Drag-to-wire state.
    (string Repo, string Input)? _dragPin;
    Point _pressPos;
    Point _dragCursor;
    bool _dragMoved;

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

        var g = Graph;
        _laidOut = g; // the dictionaries below correspond to this snapshot; Render uses it too
        if (g is null) { _height = 200; return; }

        // Ports: a rail down the left.
        for (var i = 0; i < g.Ports.Count; i++)
        {
            var y = RailTop + i * PortGap;
            var rect = new Rect(PortX, y, PortW, PortH);
            _portRects[g.Ports[i].Name] = rect;
            _portAnchor[g.Ports[i].Name] = new Point(rect.Right, rect.Center.Y);
        }
        var portsBottom = RailTop + Math.Max(0, g.Ports.Count) * PortGap + 12;

        // Repos: stacked on the right, pins on their left edge (facing the rail).
        var repoX = BoardW - 24 - NodeW;
        var y2 = RepoTop;
        foreach (var repo in g.Repos)
        {
            var h = HeadH + Math.Max(1, repo.Pins.Count) * RowH + NodePad;
            var rect = new Rect(repoX, y2, NodeW, h);
            _repoBoxes.Add((repo.Repo, rect, repo));

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

        _height = Math.Max(portsBottom, y2) + 12;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(0, 0, BoardW, _height));
        var g = _laidOut; // draw the snapshot the layout dictionaries were built from
        if (g is null) return;

        // Cables first, so nodes sit on top.
        foreach (var e in g.Edges)
        {
            if (!_pins.TryGetValue((e.Repo, e.Input), out var pin)) continue;
            if (!_portAnchor.TryGetValue(e.Port, out var start)) continue;

            var dim = _dragPin is null && _hoverPort is not null && _hoverPort != e.Port;
            var colour = e.Transform ? Xform : Wire;
            Pen pen;
            if (dim)
            {
                var c = ((ISolidColorBrush)colour).Color;
                pen = new Pen(new SolidColorBrush(c, 0.12), 1);
            }
            else
            {
                pen = new Pen(colour, e.Shared ? 3 : 2.2) { LineCap = PenLineCap.Round };
            }
            ctx.DrawGeometry(null, pen, Cable(start, pin.Pt));
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
                if (port.Shared)
                    DrawText(ctx, "SHARED ×" + port.ConsumerCount,
                        new Rect(rect.X, rect.Y - 15, rect.Width, 13), Wire, 9, center: true);
                // Outlet dot on the right edge.
                ctx.DrawEllipse(border, null, anchor, 3.5, 3.5);
            }
        }

        // Repo nodes (right) with pins on their left edge.
        foreach (var (repoName, node, repo) in _repoBoxes)
        {
            ctx.DrawRectangle(Panel, new Pen(Border, 1.5), node, 12, 12);
            ctx.DrawRectangle(PanelHead, null, new Rect(node.X, node.Y, NodeW, HeadH), 12, 12);
            DrawText(ctx, repoName, new Rect(node.X + 14, node.Y, NodeW - 22, HeadH), Title, 13, vcenter: true);

            foreach (var pin in repo.Pins)
            {
                if (!_pins.TryGetValue((repoName, pin.Input), out var pv)) continue;
                var (pt, bound, problem) = pv;
                DrawText(ctx, pin.Input, new Rect(node.X + 16, pt.Y - RowH / 2, NodeW - 26, RowH),
                    problem ? Danger : Fg, 12, vcenter: true);

                var jackBrush = problem ? PanelHead : (bound ? Wire : PanelHead);
                var jackPen = new Pen(problem ? Danger : (bound ? Wire : Border), 2);
                ctx.DrawEllipse(jackBrush, jackPen, pt, 5.5, 5.5);
            }
        }

        // Rubber-band cable while dragging a pin toward a port.
        if (_dragPin is { } dp && _dragMoved && _pins.TryGetValue(dp, out var src))
        {
            var pen = new Pen(Wire, 2.5) { DashStyle = new DashStyle([2, 3], 0), LineCap = PenLineCap.Round };
            ctx.DrawGeometry(null, pen, Cable(src.Pt, _dragCursor));
        }

        if (_hoverPort is not null && _dragPin is null) DrawTooltip(ctx, g, _hoverPort);
    }

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
        if (IsEditable && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && PinAt(e.GetPosition(this)) is { } pin)
        {
            _dragPin = pin;
            _pressPos = _dragCursor = e.GetPosition(this);
            _dragMoved = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_dragPin is not null)
        {
            _dragCursor = pos;
            if (!_dragMoved && Distance(_pressPos, pos) > 4) _dragMoved = true;
            _hoverPort = PortAt(pos); // drop-target
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }

        var hit = PortAt(pos);
        var changed = hit != _hoverPort;
        _hoverPort = hit;
        _hoverPos = pos;
        // Redraw on enter/leave, and on every move while over a port so the tooltip follows the cursor.
        if (changed || hit is not null) InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragPin is { } pin)
        {
            var pos = e.GetPosition(this);
            e.Pointer.Capture(null);
            _dragPin = null;
            _hoverPort = null;

            if (_dragMoved)
            {
                if (PortAt(pos) is { } port)
                    WireCommand?.Execute(new WireRequest(pin.Repo, pin.Input, port));
                else if (IsBound(pin))
                    UnwireCommand?.Execute(new PinRef(pin.Repo, pin.Input)); // dragged off → unbind
            }
            else if (IsBound(pin))
            {
                OpenPinMenu(pin); // a click on a bound pin → transform / unbind menu
            }

            _dragMoved = false;
            e.Handled = true;
            InvalidateVisual();
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverPort is not null && _dragPin is null) { _hoverPort = null; InvalidateVisual(); }
        base.OnPointerExited(e);
    }

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

    bool IsBound((string Repo, string Input) pin) => _pins.TryGetValue(pin, out var v) && v.Bound;

    static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    void OpenPinMenu((string Repo, string Input) pin)
    {
        var flyout = new MenuFlyout();
        foreach (var preset in TransformPresets.All.Where(p => p != TransformPresets.Custom))
        {
            var item = new MenuItem { Header = preset.Label };
            item.Click += (_, _) => TransformCommand?.Execute(new TransformRequest(pin.Repo, pin.Input, preset));
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        var unbind = new MenuItem { Header = "Unbind" };
        unbind.Click += (_, _) => UnwireCommand?.Execute(new PinRef(pin.Repo, pin.Input));
        flyout.Items.Add(unbind);
        flyout.ShowAt(this, showAtPointer: true);
    }
}
