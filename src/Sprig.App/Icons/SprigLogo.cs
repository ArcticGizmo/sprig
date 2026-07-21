using Avalonia;
using Avalonia.Media;

namespace Sprig.App.Icons;

/// <summary>
/// The sprig sprout mark as a native Avalonia vector image. Avalonia rasterises vectors through Skia at
/// the display's real resolution, so this stays crisp at any size or DPI — unlike a fixed-size PNG,
/// which softens when scaled for the in-app header.
///
/// The path geometry and its gradient come from <c>SprigLogo.g.cs</c>, which <c>tools/IconGen</c>
/// generates straight from <c>sprig.svg</c> — the same source it rasterises the icon assets from. So
/// there is a single source of truth: edit the SVG, then run <c>dotnet run --project tools/IconGen</c>
/// (or <c>tools/gen-icons.ps1</c>) to regenerate both the raster icons and this vector data.
/// </summary>
public static partial class SprigLogo
{
    /// <summary>Builds a fresh <see cref="DrawingImage"/> of the logo, assignable to an <c>Image.Source</c>.</summary>
    public static DrawingImage Create()
    {
        // The gradient is authored in the SVG's user space (userSpaceOnUse), the same space the path
        // lives in, so absolute endpoints line the gradient up with the shape at any render size.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(GradX1, GradY1, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(GradX2, GradY2, RelativeUnit.Absolute),
        };
        foreach (var (offset, argb) in GradientStops)
            brush.GradientStops.Add(new GradientStop(Color.FromUInt32(argb), offset));

        // "F1" = nonzero winding, the SVG default — keeps the inner circle cut-outs rendering as holes.
        var drawing = new GeometryDrawing
        {
            Geometry = Geometry.Parse("F1 " + PathData),
            Brush = brush,
        };

        return new DrawingImage { Drawing = drawing };
    }
}
