using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sprig.Core.Stacks;

namespace Sprig.App.Controls;

/// <summary>
/// The patchbay: a read-only diagram of a stack's wiring. Repos sit either side of a central rail of
/// ports; a cable runs from each input pin to the port it consumes. Shared ports are highlighted,
/// transform cables are drawn in the transform colour, and unbound pins read red. Hovering a port
/// dims every cable not on it, so a shared port's fan-out is obvious. Layout is deterministic and
/// derived from <see cref="WiringGraph"/>; this control only draws.
/// </summary>
public sealed class WiringCanvas : Control
{
    public static readonly StyledProperty<WiringGraph?> GraphProperty =
        AvaloniaProperty.Register<WiringCanvas, WiringGraph?>(nameof(Graph));

    public WiringGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

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

    const double BoardW = 880, NodeW = 190, HeadH = 30, RowH = 30, NodePad = 12;
    const double PortW = 140, PortH = 28, PortGap = 62, RailTop = 44;
    const double ColTop = 24, ColGap = 22;

    Typeface _mono = new("Consolas");
    readonly Dictionary<string, Rect> _portRects = new(StringComparer.Ordinal);
    readonly Dictionary<(string Repo, string Input), (Point Pt, bool Left, bool Bound, bool Problem)> _pins = new();
    readonly Dictionary<string, bool> _repoSide = new(StringComparer.Ordinal); // repo -> isLeft
    double _height = 300;
    string? _hoverPort;

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
        _pins.Clear();
        _repoSide.Clear();

        var g = Graph;
        if (g is null) { _height = 200; return; }

        // Ports down the centre rail.
        var railX = BoardW / 2;
        for (var i = 0; i < g.Ports.Count; i++)
        {
            var y = RailTop + i * PortGap;
            _portRects[g.Ports[i].Name] = new Rect(railX - PortW / 2, y, PortW, PortH);
        }
        var portsBottom = RailTop + Math.Max(0, g.Ports.Count) * PortGap + 20;

        // Repos alternate left / right, stacked within their column.
        double leftY = ColTop, rightY = ColTop;
        foreach (var (repo, idx) in g.Repos.Select((r, i) => (r, i)))
        {
            var isLeft = idx % 2 == 0;
            _repoSide[repo.Repo] = isLeft;
            var h = HeadH + Math.Max(1, repo.Pins.Count) * RowH + NodePad;
            var x = isLeft ? 26 : BoardW - 26 - NodeW;
            var y = isLeft ? leftY : rightY;

            for (var p = 0; p < repo.Pins.Count; p++)
            {
                var pin = repo.Pins[p];
                var py = y + HeadH + p * RowH + RowH / 2;
                var px = isLeft ? x + NodeW : x;
                var problem = pin.Kind == BindingKind.Unbound || pin.UndeclaredPort;
                _pins[(repo.Repo, pin.Input)] = (new Point(px, py), isLeft, pin.HasPort, problem);
            }

            var bottom = y + h + ColGap;
            if (isLeft) leftY = bottom; else rightY = bottom;
        }

        _height = Math.Max(portsBottom, Math.Max(leftY, rightY)) + 16;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(0, 0, BoardW, _height));
        var g = Graph;
        if (g is null) return;

        // Cables first, so nodes sit on top.
        foreach (var e in g.Edges)
        {
            if (!_pins.TryGetValue((e.Repo, e.Input), out var pin)) continue;
            if (!_portRects.TryGetValue(e.Port, out var portRect)) continue;

            var dim = _hoverPort is not null && _hoverPort != e.Port;
            var colour = e.Transform ? Xform : Wire;
            Pen pen;
            if (dim)
            {
                var c = ((ISolidColorBrush)colour).Color;
                pen = new Pen(new SolidColorBrush(c, 0.14), 1);
            }
            else
            {
                pen = new Pen(colour, e.Shared ? 3 : 2.2) { LineCap = PenLineCap.Round };
            }

            var start = pin.Pt;
            var end = new Point(pin.Left ? portRect.Left : portRect.Right, portRect.Center.Y);
            ctx.DrawGeometry(null, pen, Cable(start, end, pin.Left));
        }

        // Port rail.
        foreach (var port in g.Ports)
        {
            var rect = _portRects[port.Name];
            var faded = _hoverPort is not null && _hoverPort != port.Name;
            var border = port.Shared ? Wire : (port.Used ? Signal : Border);
            var fill = port.Used ? Brush.Parse("#152417") : PanelHead;
            var pen = new Pen(border, port.Shared ? 2 : 1);
            using (ctx.PushOpacity(faded ? 0.35 : 1.0))
            {
                ctx.DrawRectangle(fill, pen, rect, 8, 8);
                DrawText(ctx, port.Name, rect, port.Used ? Signal : Muted, 12.5, center: true);
                if (port.Shared)
                    DrawText(ctx, "SHARED ×" + port.ConsumerCount,
                        new Rect(rect.X, rect.Y - 16, rect.Width, 14), Wire, 9, center: true);
            }
        }

        // Repo nodes with pins.
        foreach (var repo in g.Repos)
        {
            var isLeft = _repoSide[repo.Repo];
            var x = isLeft ? 26 : BoardW - 26 - NodeW;
            var first = repo.Pins.Count > 0 ? _pins[(repo.Repo, repo.Pins[0].Input)] : default;
            var top = (repo.Pins.Count > 0 ? first.Pt.Y - RowH / 2 - HeadH : ColTop);
            var h = HeadH + Math.Max(1, repo.Pins.Count) * RowH + NodePad;
            var node = new Rect(x, top, NodeW, h);

            ctx.DrawRectangle(Panel, new Pen(Border, 1.5), node, 12, 12);
            ctx.DrawRectangle(PanelHead, null, new Rect(x, top, NodeW, HeadH), 12, 12);
            DrawText(ctx, repo.Repo, new Rect(x + 12, top, NodeW - 20, HeadH), Title, 13, vcenter: true);

            foreach (var pin in repo.Pins)
            {
                var (pt, left, bound, problem) = _pins[(repo.Repo, pin.Input)];
                var labelRect = new Rect(x + (left ? 14 : 22), pt.Y - RowH / 2, NodeW - 30, RowH);
                DrawText(ctx, pin.Input, labelRect, problem ? Danger : Fg, 12, vcenter: true);

                var jackBrush = problem ? PanelHead : (pin.Shared ? Wire : (bound ? Wire : PanelHead));
                var jackPen = new Pen(problem ? Danger : (bound ? Wire : Border), 2);
                ctx.DrawEllipse(jackBrush, jackPen, pt, 5.5, 5.5);
            }
        }
    }

    static StreamGeometry Cable(Point a, Point b, bool leftSource)
    {
        var dx = Math.Abs(b.X - a.X) * 0.5;
        var c1 = new Point(leftSource ? a.X + dx : a.X - dx, a.Y);
        var c2 = new Point(leftSource ? b.X - dx : b.X + dx, b.Y);
        var geo = new StreamGeometry();
        using var c = geo.Open();
        c.BeginFigure(a, false);
        c.CubicBezierTo(c1, c2, b);
        c.EndFigure(false);
        return geo;
    }

    void DrawText(DrawingContext ctx, string text, Rect within, IBrush brush, double size,
        bool center = false, bool vcenter = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _mono, size, brush);
        var x = center ? within.X + (within.Width - ft.Width) / 2 : within.X;
        var y = (center || vcenter) ? within.Y + (within.Height - ft.Height) / 2 : within.Y;
        ctx.DrawText(ft, new Point(x, y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        var hit = _portRects.FirstOrDefault(kv => kv.Value.Contains(pos)).Key;
        if (hit != _hoverPort) { _hoverPort = hit; InvalidateVisual(); }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverPort is not null) { _hoverPort = null; InvalidateVisual(); }
        base.OnPointerExited(e);
    }
}
