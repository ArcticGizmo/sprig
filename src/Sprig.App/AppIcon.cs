using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>
/// The icon for sprig's windows and tray. A dev build has its colours inverted, so with an installed sprig in the tray
/// beside it the two can be told apart at a glance.
/// </summary>
public static class AppIcon
{
    static readonly Lazy<WindowIcon> Loaded = new(() => AppProfile.IsDev ? Inverted() : new WindowIcon(Open("sprig.ico")));

    public static WindowIcon Current => Loaded.Value;

    static System.IO.Stream Open(string asset)
        => AssetLoader.Open(new Uri($"avares://{typeof(AppIcon).Assembly.GetName().Name}/Assets/{asset}"));

    static WindowIcon Inverted()
    {
        using var source = new Bitmap(Open("sprig.png"));
        var size = source.PixelSize;
        var alpha = source.AlphaFormat ?? AlphaFormat.Premul;
        var inverted = new WriteableBitmap(size, source.Dpi, source.Format ?? PixelFormat.Bgra8888, alpha);
        using (var frame = inverted.Lock())
        {
            var length = frame.RowBytes * size.Height;
            source.CopyPixels(new PixelRect(size), frame.Address, length, frame.RowBytes);
            var pixels = new byte[length];
            Marshal.Copy(frame.Address, pixels, 0, length);
            Invert(pixels, premultiplied: alpha == AlphaFormat.Premul);
            Marshal.Copy(pixels, 0, frame.Address, length);
        }
        return new WindowIcon(inverted);
    }

    /// <summary>Each colour channel of 32-bit pixels with alpha last, inverted against its alpha when premultiplied so
    /// the transparent edges stay transparent.</summary>
    internal static void Invert(byte[] pixels, bool premultiplied)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var top = premultiplied ? pixels[i + 3] : (byte)255;
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Max(0, top - pixels[i + c]);
        }
    }
}
