using System.Globalization;
using System.Text.RegularExpressions;

namespace Sprig.Engine.Hosting;

/// <summary>A stretch of a log line in one style.</summary>
/// <param name="Foreground">0xRRGGBB, or null for the default text colour.</param>
public sealed record AnsiRun(string Text, int? Foreground = null, bool Bold = false, bool Dim = false);

/// <summary>Terminal escape sequences in app output: colours, cursor moves and window titles.</summary>
public static partial class AnsiText
{
    /// <summary>The text as it reads, for matching against or writing somewhere that doesn't render colour.</summary>
    public static string Strip(string text) => text.Contains('\x1B') ? Escape().Replace(text, "") : text;

    /// <summary>
    /// The line split into styled runs, following the colour and weight codes (SGR) apps print. Backgrounds and every
    /// other sequence are dropped: a log pane shows text on its own background.
    /// </summary>
    public static IReadOnlyList<AnsiRun> Runs(string line)
    {
        if (!line.Contains('\x1B')) return line.Length == 0 ? [] : [new AnsiRun(line)];
        var runs = new List<AnsiRun>();
        int? fg = null;
        bool bold = false, dim = false;
        var at = 0;
        foreach (Match m in Escape().Matches(line))
        {
            if (m.Index > at) runs.Add(new AnsiRun(line[at..m.Index], fg, bold, dim));
            at = m.Index + m.Length;
            if (m.Value.Length >= 3 && m.Value[1] == '[' && m.Value[^1] == 'm') Apply(m.Value[2..^1], ref fg, ref bold, ref dim);
        }
        if (at < line.Length) runs.Add(new AnsiRun(line[at..], fg, bold, dim));
        return runs;
    }

    static void Apply(string parameters, ref int? fg, ref bool bold, ref bool dim)
    {
        var codes = parameters.Length == 0 ? [0] : parameters.Split(';').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            switch (code)
            {
                case 0: fg = null; bold = false; dim = false; break;
                case 1: bold = true; break;
                case 2: dim = true; break;
                case 22: bold = false; dim = false; break;
                case >= 30 and <= 37: fg = Palette[code - 30]; break;
                case 39: fg = null; break;
                case >= 90 and <= 97: fg = Palette[code - 90 + 8]; break;
                case 38 or 48 when i + 2 < codes.Length && codes[i + 1] == 5:
                    if (code == 38) fg = Xterm256(codes[i + 2]);
                    i += 2;
                    break;
                case 38 or 48 when i + 4 < codes.Length && codes[i + 1] == 2:
                    if (code == 38) fg = (Math.Clamp(codes[i + 2], 0, 255) << 16) | (Math.Clamp(codes[i + 3], 0, 255) << 8) | Math.Clamp(codes[i + 4], 0, 255);
                    i += 4;
                    break;
            }
        }
    }

    /// <summary>The 16 basic colours, tuned to read on a dark background (pure blue and black wouldn't).</summary>
    static readonly int[] Palette =
    [
        0x6B6B7B, 0xF87171, 0x4ADE80, 0xFBBF24, 0x60A5FA, 0xC084FC, 0x22D3EE, 0xE1E1EB,
        0x8C8CA0, 0xFCA5A5, 0x86EFAC, 0xFDE68A, 0x93C5FD, 0xD8B4FE, 0x67E8F9, 0xFFFFFF,
    ];

    static int Xterm256(int n)
    {
        n = Math.Clamp(n, 0, 255);
        if (n < 16) return Palette[n];
        if (n >= 232)
        {
            var grey = 8 + (n - 232) * 10;
            return (grey << 16) | (grey << 8) | grey;
        }
        n -= 16;
        static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
        return (Level(n / 36) << 16) | (Level(n / 6 % 6) << 8) | Level(n % 6);
    }

    // CSI (ESC [ ... final byte), OSC (ESC ] ... BEL or ESC \), and the two-byte forms.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Escape();
}
