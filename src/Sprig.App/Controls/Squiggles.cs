using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sprig.App.Authoring;

namespace Sprig.App.Controls;

/// <summary>A compile problem's place in the document. Kept in a segment collection tied to the document, so it moves
/// with the code as it's typed above it until the next check replaces it.</summary>
public sealed class ProblemMarker(HostDiagnostic problem) : TextSegment
{
    public HostDiagnostic Problem { get; } = problem;
    public bool IsError => Problem.Severity == "Error";
}

/// <summary>Wavy underlines under compile errors (red) and warnings (amber), as an IDE draws them.</summary>
public sealed class Squiggles : IBackgroundRenderer
{
    static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#F87171")), 1.2);
    static readonly IPen WarningPen = new Pen(new SolidColorBrush(Color.Parse("#FBBF24")), 1.2);

    TextSegmentCollection<ProblemMarker> _markers = new();

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Start over on <paramref name="document"/>, as when the editor's whole text is replaced.</summary>
    public void Attach(TextDocument document) => _markers = new TextSegmentCollection<ProblemMarker>(document);

    public void Show(TextDocument document, IEnumerable<HostDiagnostic> problems)
    {
        _markers.Clear();
        foreach (var problem in problems)
        {
            var start = Math.Clamp(problem.Start, 0, document.TextLength);
            var end = Math.Clamp(problem.End, start, document.TextLength);
            // A zero-width problem (a missing ';') still needs something to underline.
            if (end == start) (start, end) = end < document.TextLength ? (start, end + 1) : (Math.Max(0, start - 1), end);
            _markers.Add(new ProblemMarker(problem) { StartOffset = start, Length = end - start });
        }
    }

    /// <summary>Problems under <paramref name="offset"/>, errors first.</summary>
    public IReadOnlyList<HostDiagnostic> At(int offset)
        => _markers.FindSegmentsContaining(offset).OrderBy(m => m.IsError ? 0 : 1).Select(m => m.Problem).ToArray();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || _markers.Count == 0) return;
        var visible = textView.VisualLines;
        if (visible.Count == 0) return;
        var from = visible[0].FirstDocumentLine.Offset;
        var to = visible[^1].LastDocumentLine.EndOffset;

        foreach (var marker in _markers.FindOverlappingSegments(from, to - from).OrderBy(m => m.IsError))
        {
            var pen = marker.IsError ? ErrorPen : WarningPen;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                drawingContext.DrawGeometry(null, pen, Wave(rect));
        }
    }

    static StreamGeometry Wave(Rect rect)
    {
        const double step = 2.5;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var y = rect.Bottom - 1.5;
        context.BeginFigure(new Point(rect.Left, y), false);
        var up = true;
        for (var x = rect.Left + step; x <= rect.Right + step; x += step, up = !up)
            context.LineTo(new Point(Math.Min(x, rect.Right), up ? y - 2 : y));
        context.EndFigure(false);
        return geometry;
    }
}
