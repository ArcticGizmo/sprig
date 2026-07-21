using System.Text.RegularExpressions;

namespace Sprig.App.Controls;

/// <summary>
/// Pure, UI-free logic for <c>${sprig.*}</c> tokens: build the candidate completions from the
/// variables available to a repo (<c>workspace</c> + its declared inputs), decide whether what's
/// being typed is completing an open token, splice a chosen token back in, and flag references that
/// name a variable that doesn't exist. Kept separate from the control so it can be unit-tested.
/// The reference grammar mirrors <c>Sprig.Core.Config.ConfigReferences</c> exactly.
/// </summary>
public static partial class SprigTokenCompletion
{
    // Same pattern the config validator uses to find references, so "valid here" matches "valid on save".
    [GeneratedRegex(@"\$\{sprig\.([^}]+)\}")]
    private static partial Regex RefPattern();

    /// <summary>The full completion tokens offered — one <c>${sprig.&lt;name&gt;}</c> per available variable.</summary>
    public static IReadOnlyList<string> Tokens(IEnumerable<string> variableNames)
    {
        var tokens = new List<string>();
        foreach (var name in variableNames)
        {
            var n = name?.Trim();
            if (!string.IsNullOrEmpty(n))
            {
                var token = $"${{sprig.{n}}}";
                if (!tokens.Contains(token)) tokens.Add(token);
            }
        }
        return tokens;
    }

    /// <summary>
    /// The trailing, still-open <c>${…</c> fragment of <paramref name="text"/> (from the last
    /// <c>${</c> to the end), or null when the end of the text isn't inside an open token — no
    /// <c>${</c>, or the last one is already closed by a <c>}</c>.
    /// </summary>
    public static string? TrailingFragment(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var open = text.LastIndexOf("${", StringComparison.Ordinal);
        if (open < 0)
            return null;
        if (text.IndexOf('}', open) >= 0)
            return null; // the last ${ is already closed — not completing a token
        return text[open..];
    }

    /// <summary>Whether <paramref name="item"/> completes the trailing token fragment of <paramref name="search"/>.</summary>
    public static bool Matches(string? search, string? item)
    {
        var fragment = TrailingFragment(search);
        return fragment is not null && item is not null
            && item.StartsWith(fragment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replace the trailing <c>${…</c> fragment of <paramref name="text"/> with <paramref name="item"/>,
    /// preserving any literal prefix (e.g. <c>http://localhost:</c>). No-op if there's no open fragment.</summary>
    public static string Combine(string? text, string? item)
    {
        text ??= string.Empty;
        item ??= string.Empty;
        var open = text.LastIndexOf("${", StringComparison.Ordinal);
        if (open < 0 || text.IndexOf('}', open) >= 0)
            return text;
        return text[..open] + item;
    }

    /// <summary>
    /// Accept <paramref name="item"/> as the completion for the token being edited at <paramref name="caret"/>,
    /// returning the new text and caret position. Replaces the <em>whole</em> token — from its opening
    /// <c>${</c> through its closing <c>}</c> — so picking a suggestion mid-token doesn't leave the old
    /// tail behind (the bug where editing inside <c>${sprig.db|Port}</c> produced <c>${sprig.dbPort}Port}</c>).
    /// Only meant to be called while a completion is open, i.e. with no <c>}</c> between the open and the caret.
    /// </summary>
    public static (string Text, int Caret) Replace(string? text, int caret, string? item)
    {
        text ??= string.Empty;
        item ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        var open = text[..caret].LastIndexOf("${", StringComparison.Ordinal);
        if (open < 0)
            return (text, caret); // not inside a token — nothing to replace

        // Consume the token's tail after the caret up to its closing '}', unless another token starts
        // first (this one is unclosed) — then stop at the caret so we don't swallow the next token.
        var close = text.IndexOf('}', caret);
        var nextOpen = text.IndexOf("${", caret, StringComparison.Ordinal);
        var end = close >= 0 && (nextOpen < 0 || close < nextOpen) ? close + 1 : caret;

        return (text[..open] + item + text[end..], open + item.Length);
    }

    /// <summary>
    /// The <c>${sprig.&lt;name&gt;}</c> references in <paramref name="value"/> that name a variable not in
    /// <paramref name="variableNames"/> (Ordinal, whitespace-trimmed — same as the validator). A
    /// still-being-typed, unclosed token isn't a reference yet, so it's never flagged.
    /// </summary>
    public static IReadOnlyList<string> UnknownReferences(string? value, IEnumerable<string> variableNames)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        var known = new HashSet<string>(variableNames.Select(n => n.Trim()), StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (Match m in RefPattern().Matches(value))
        {
            var name = m.Groups[1].Value.Trim();
            if (!known.Contains(name) && !unknown.Contains(name))
                unknown.Add(name);
        }
        return unknown;
    }

    /// <summary>True when every <c>${sprig.*}</c> reference in <paramref name="value"/> names a known variable.</summary>
    public static bool IsValid(string? value, IEnumerable<string> variableNames)
        => UnknownReferences(value, variableNames).Count == 0;
}
