using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Svg;

// Generates every icon asset from the single source-of-truth SVG (sprig.svg).
//
//   src/Sprig.App/Assets/sprig.png    256x256 PNG — general/window icon
//   src/Sprig.App/Assets/sprig.ico    multi-resolution ICO (16..256) — window + .exe ApplicationIcon
//   landing-icon.png                   512x512 PNG — the README header logo
//   src/Sprig.App/Icons/SprigLogo.g.cs vector data the in-app logo is drawn from (Skia via Avalonia)
//
// Re-run after editing sprig.svg:  dotnet run --project tools/IconGen   (or tools/gen-icons.ps1).
//
// Adapted from Claudoscope's IconGen. sprig's logo is a single gradient-filled path (not solid-colour
// wedges), so WriteSprigLogo extracts the path + its linear gradient rather than a list of blades.

// Resolve the repo root from this tool's location so it works regardless of CWD.
string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string svgPath = Path.Combine(repoRoot, "sprig.svg");
if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"Source SVG not found: {svgPath}");
    return 1;
}

Console.WriteLine($"Source: {svgPath}");
var doc = SvgDocument.Open(svgPath);

// Normalize the viewport to the viewBox in user units. Inkscape exports set width/height in real-world
// units (sprig.svg is "256mm"), which makes Svg.NET apply an extra viewBox->viewport scale that throws
// off doc.Bounds and the crop transform. Pinning width/height to the viewBox makes that transform
// identity, so content bounds line up with user coordinates.
if (doc.ViewBox.Width > 0 && doc.ViewBox.Height > 0)
{
    doc.Width = new SvgUnit(SvgUnitType.User, doc.ViewBox.Width);
    doc.Height = new SvgUnit(SvgUnitType.User, doc.ViewBox.Height);
}

// The artwork may not fill its viewBox — there can be transparent margin. Crop to the actual drawn
// content, re-center it in a square, and scale that to fill the frame. PAD keeps a sliver of breathing
// room so anti-aliased edges don't clip.
const float PAD = 0.02f;
var fit = ComputeFit(doc, PAD);
float vbSide = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : fit.Side;
Console.WriteLine($"Fit: content {fit.Box.Width:0.0}x{fit.Box.Height:0.0} cropped from {vbSide:0}x{vbSide:0} viewBox ({vbSide / fit.Side:0.00}x larger)");

// The .ico ships a true frame at each size so Windows never downscales at runtime (the taskbar asks
// for small frames; large surfaces ask for 256).
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };

string assetsDir = Path.Combine(repoRoot, "src", "Sprig.App", "Assets");
WritePng(doc, fit, Path.Combine(assetsDir, "sprig.png"), 256);
WritePng(doc, fit, Path.Combine(repoRoot, "landing-icon.png"), 512);
WriteIco(doc, fit, Path.Combine(assetsDir, "sprig.ico"), icoSizes);

// The in-app logo draws as a native Avalonia vector (Avalonia rasterises it through Skia at the
// display's real resolution) so it stays crisp at any size/DPI. Emit its geometry + gradient from the
// same SVG so the SVG stays the single source of truth (SprigLogo.Create() turns this into a DrawingImage).
WriteSprigLogo(svgPath, Path.Combine(repoRoot, "src", "Sprig.App", "Icons", "SprigLogo.g.cs"));

Console.WriteLine("Done.");
return 0;

// Renders the cropped, re-centered content square at the given pixel size with high-quality
// anti-aliasing and a transparent background.
static Bitmap Render(SvgDocument doc, Fit fit, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    bmp.SetResolution(96, 96);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    // Map the padded content square onto the pixel canvas: shift its top-left to 0,0 then scale up to
    // fill. Prepend order means the translate is applied first.
    float scale = size / fit.Side;
    g.ScaleTransform(scale, scale);
    g.TranslateTransform(-fit.OriginX, -fit.OriginY);
    doc.Draw(g);
    return bmp;
}

static byte[] RenderPng(SvgDocument doc, Fit fit, int size)
{
    using var bmp = Render(doc, fit, size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static void WritePng(SvgDocument doc, Fit fit, string path, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, RenderPng(doc, fit, size));
    Console.WriteLine($"  {Path.GetFileName(path)}  {size}x{size}");
}

// Writes a Vista+ ICO whose frames are PNG-compressed (keeps the file small and supports 256px).
static void WriteIco(SvgDocument doc, Fit fit, string path, int[] sizes)
{
    var frames = sizes.Select(s => RenderPng(doc, fit, s)).ToArray();

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR header
    w.Write((ushort)0);             // reserved
    w.Write((ushort)1);             // type = icon
    w.Write((ushort)sizes.Length);  // image count

    // Each ICONDIRENTRY is 16 bytes; image data follows the full directory.
    int offset = 6 + sizes.Length * 16;
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
        w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        w.Write((byte)0);                        // palette count
        w.Write((byte)0);                        // reserved
        w.Write((ushort)1);                      // colour planes
        w.Write((ushort)32);                     // bits per pixel
        w.Write(frames[i].Length);               // bytes of image data
        w.Write(offset);                         // offset of image data
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
        w.Write(frame);

    Console.WriteLine($"  {Path.GetFileName(path)}  [{string.Join(", ", sizes)}]");
}

// Reads sprig's single gradient-filled path straight from the XML (not the Svg.NET model, so the
// emitted path string is byte-for-byte what the file holds) plus the linear gradient that fills it,
// and writes them as a generated partial of SprigLogo. The app builds an Avalonia DrawingImage from
// these — see SprigLogo.Create().
static void WriteSprigLogo(string svgPath, string outPath)
{
    XNamespace ns = "http://www.w3.org/2000/svg";
    XNamespace xlink = "http://www.w3.org/1999/xlink";
    var root = XDocument.Load(svgPath).Root!;

    // The sprout is the lone <path>.
    var path = root.Descendants(ns + "path").First();
    string data = ((string)path.Attribute("d")!).Trim();

    // Its fill is a url(#gradient) reference, held in the style attribute (Inkscape) or a fill attribute.
    string? fill = StyleProp(path, "fill") ?? (string?)path.Attribute("fill");
    string gradId = UrlId(fill ?? throw new InvalidOperationException("path has no fill"));

    // Index the gradients so we can resolve the reference (and any xlink:href stop inheritance).
    var grads = root.Descendants(ns + "linearGradient")
        .ToDictionary(g => (string)g.Attribute("id")!, g => g);
    var grad = grads[gradId];

    // Coordinates live on the referencing gradient; the stops may be inherited via xlink:href.
    double x1 = Dbl(grad, "x1"), y1 = Dbl(grad, "y1"), x2 = Dbl(grad, "x2"), y2 = Dbl(grad, "y2");

    var stopSource = grad;
    if (!grad.Elements(ns + "stop").Any())
    {
        var href = (string?)grad.Attribute(xlink + "href") ?? (string?)grad.Attribute("href");
        if (href is { Length: > 0 }) stopSource = grads[href.TrimStart('#')];
    }
    var stops = stopSource.Elements(ns + "stop")
        .Select(s => (Offset: Dbl(s, "offset"), Argb: StopArgb(s)))
        .ToList();

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated>");
    sb.AppendLine("// Generated from sprig.svg by tools/IconGen. Do not edit by hand.");
    sb.AppendLine("// Regenerate with: dotnet run --project tools/IconGen  (or tools/gen-icons.ps1).");
    sb.AppendLine("// </auto-generated>");
    sb.AppendLine();
    sb.AppendLine("namespace Sprig.App.Icons;");
    sb.AppendLine();
    sb.AppendLine("public static partial class SprigLogo");
    sb.AppendLine("{");
    sb.AppendLine("    // The sprout path (SVG 'd'), in the SVG's user space.");
    sb.AppendLine($"    private const string PathData = {Quote(data)};");
    sb.AppendLine();
    sb.AppendLine("    // Linear gradient (userSpaceOnUse) that fills the path: endpoints then colour stops.");
    sb.AppendLine($"    private const double GradX1 = {F(x1)}, GradY1 = {F(y1)}, GradX2 = {F(x2)}, GradY2 = {F(y2)};");
    sb.AppendLine("    private static readonly (double Offset, uint Argb)[] GradientStops =");
    sb.AppendLine("    [");
    foreach (var (offset, argb) in stops)
        sb.AppendLine($"        ({F(offset)}, 0x{argb:X8}u),");
    sb.AppendLine("    ];");
    sb.AppendLine("}");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, sb.ToString());
    Console.WriteLine($"  {Path.GetFileName(outPath)}  [{stops.Count} stops]");
}

// Value of a property inside an element's style="a:b;c:d" attribute, or null if absent.
static string? StyleProp(XElement e, string prop)
{
    var style = (string?)e.Attribute("style");
    if (string.IsNullOrEmpty(style)) return null;
    foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var i = part.IndexOf(':');
        if (i > 0 && part[..i].Trim().Equals(prop, StringComparison.OrdinalIgnoreCase))
            return part[(i + 1)..].Trim();
    }
    return null;
}

// "url(#foo)" -> "foo".
static string UrlId(string fill)
{
    int lp = fill.IndexOf('#'), rp = fill.IndexOf(')');
    return rp > lp ? fill[(lp + 1)..rp] : fill.TrimStart('#');
}

// A <stop> with stop-color + stop-opacity (in either style or attributes) -> 0xAARRGGBB.
static uint StopArgb(XElement stop)
{
    string? color = StyleProp(stop, "stop-color") ?? (string?)stop.Attribute("stop-color");
    string? opacity = StyleProp(stop, "stop-opacity") ?? (string?)stop.Attribute("stop-opacity");

    byte r = 0, g = 0, b = 0;
    color = color?.Trim();
    if (color is { Length: 7 } && color[0] == '#')
    {
        r = Convert.ToByte(color.Substring(1, 2), 16);
        g = Convert.ToByte(color.Substring(3, 2), 16);
        b = Convert.ToByte(color.Substring(5, 2), 16);
    }
    double op = 1.0;
    if (!string.IsNullOrWhiteSpace(opacity))
        double.TryParse(opacity, NumberStyles.Float, CultureInfo.InvariantCulture, out op);
    byte a = (byte)Math.Round(Math.Clamp(op, 0, 1) * 255);
    return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
}

static double Dbl(XElement e, string attr) => double.Parse((string)e.Attribute(attr)!, CultureInfo.InvariantCulture);

// Round-trippable invariant form so the emitted C# literal reproduces the source number exactly.
static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

// SVG path data is plain ASCII (letters/digits/.,-/space) with no quotes or backslashes, so a verbatim
// string literal is safe and readable.
static string Quote(string s) => "\"" + s + "\"";

// Walks up from the tool's binary location to the directory that holds sprig.svg.
static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "sprig.svg")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // Fall back to five-up from bin/<cfg>/<tfm> if the marker walk fails.
    return Path.GetFullPath(Path.Combine(start, "..", "..", "..", "..", ".."));
}

// Computes the square crop that tightly frames the SVG's drawn content (so the raster uses the whole
// canvas instead of inheriting the viewBox's transparent margin). The square is the larger content
// dimension, centered on the content, grown by `pad` on every side.
static Fit ComputeFit(SvgDocument doc, float pad)
{
    var box = doc.Bounds;
    if (box.Width <= 0 || box.Height <= 0)
    {
        float w = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : 1f;
        float h = doc.ViewBox.Height > 0 ? doc.ViewBox.Height : 1f;
        box = new RectangleF(0, 0, w, h);
    }
    float side = Math.Max(box.Width, box.Height);
    side += side * pad * 2f;
    float cx = box.X + box.Width / 2f;
    float cy = box.Y + box.Height / 2f;
    return new Fit(box, side, cx - side / 2f, cy - side / 2f);
}

// The crop geometry: the content's own bounds, the side of the padded square that frames it, and that
// square's top-left corner in SVG user units.
readonly record struct Fit(RectangleF Box, float Side, float OriginX, float OriginY);
