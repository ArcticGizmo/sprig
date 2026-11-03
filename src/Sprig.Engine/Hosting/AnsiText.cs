using System.Text.RegularExpressions;

namespace Sprig.Engine.Hosting;

/// <summary>Terminal escape sequences in app output: colours, cursor moves and window titles.</summary>
public static partial class AnsiText
{
    /// <summary>The text as it reads, for matching against or writing somewhere that doesn't render colour.</summary>
    public static string Strip(string text) => text.Contains('\x1B') ? Escape().Replace(text, "") : text;

    // CSI (ESC [ ... final byte), OSC (ESC ] ... BEL or ESC \), and the two-byte forms.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Escape();
}
