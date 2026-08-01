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

    // What a still-open token fragment may look like: a '$', an optional '{', then the token body so far
    // (letters, digits, '_', '-', '.'). Lets a bare '$' — or '$vite' with no braces — count as "still typing
    // a token", not just '${…'. Anything else after the '$' (a space, ':', '/') means it isn't a token.
    [GeneratedRegex(@"^\$\{?[\w.\-]*$")]
    private static partial Regex OpenFragmentPattern();

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
    /// The trailing, still-open token fragment of <paramref name="text"/> — from the last <c>$</c> that
    /// begins an unclosed, token-shaped run to the end (so a bare <c>$</c>, <c>${</c>, or <c>$vite</c> all
    /// count). Null when the end of the text isn't inside such a run — no <c>$</c>, the last one is already
    /// closed by a <c>}</c>, or what follows it isn't token-shaped (e.g. a literal <c>$5.00</c> with a colon).
    /// </summary>
    public static string? TrailingFragment(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var open = OpenStart(text);
        return open < 0 ? null : text[open..];
    }

    /// <summary>Index of the <c>$</c> that begins the trailing open token, or -1. Shared by fragment
    /// detection and splicing so they always agree on where the token starts.</summary>
    static int OpenStart(string text)
    {
        var dollar = text.LastIndexOf('$');
        if (dollar < 0)
            return -1;
        var fragment = text[dollar..];
        if (fragment.IndexOf('}') >= 0)
            return -1; // already closed — not completing a token
        return OpenFragmentPattern().IsMatch(fragment) ? dollar : -1;
    }

    /// <summary>
    /// Whether <paramref name="item"/> completes the trailing token fragment of <paramref name="search"/>.
    /// Matches either the classic prefix (typing out <c>${sprig.&lt;path&gt;</c>) or the shorthand: the typed
    /// text after the <c>$</c> against any dot-segment of the token's path — so <c>$vite</c> offers both
    /// <c>${sprig.vite}</c> and <c>${sprig.ports.vite_url}</c>.
    /// </summary>
    public static bool Matches(string? search, string? item)
    {
        var fragment = TrailingFragment(search);
        if (fragment is null || item is null)
            return false;

        // Classic: still typing the literal ${sprig.<path> prefix (also covers a bare "$" → all tokens).
        if (item.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            return true;

        // Shorthand: match the typed query against the token path or any of its dot-segment suffixes.
        var query = Query(fragment);
        var path = TokenPath(item);
        if (path is null)
            return false;
        if (query.Length == 0)
            return true;
        return SegmentCandidates(path).Any(c => c.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The typed filter text: the fragment with its leading <c>$</c>, optional <c>{</c>, and the
    /// <c>sprig.</c> prefix stripped (e.g. <c>${sprig.po</c> → <c>po</c>, <c>$vite</c> → <c>vite</c>).</summary>
    static string Query(string fragment)
    {
        var s = fragment;
        if (s.StartsWith('$')) s = s[1..];
        if (s.StartsWith('{')) s = s[1..];
        if (s.StartsWith("sprig.", StringComparison.OrdinalIgnoreCase)) s = s["sprig.".Length..];
        return s;
    }

    /// <summary>The <c>&lt;path&gt;</c> of a <c>${sprig.&lt;path&gt;}</c> completion token, or null if it isn't one.</summary>
    static string? TokenPath(string token)
    {
        const string prefix = "${sprig.";
        return token.StartsWith(prefix, StringComparison.Ordinal) && token.EndsWith('}')
            ? token[prefix.Length..^1]
            : null;
    }

    /// <summary>A path plus the tail after each <c>.</c> — the "anything to the right of a dot" candidates
    /// (<c>ports.vite_url</c> → <c>ports.vite_url</c>, <c>vite_url</c>).</summary>
    static IEnumerable<string> SegmentCandidates(string path)
    {
        yield return path;
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '.')
                yield return path[(i + 1)..];
    }

    /// <summary>Replace the trailing open token fragment of <paramref name="text"/> with <paramref name="item"/>,
    /// preserving any literal prefix (e.g. <c>http://localhost:</c>). No-op if there's no open fragment.</summary>
    public static string Combine(string? text, string? item)
    {
        text ??= string.Empty;
        item ??= string.Empty;
        var open = OpenStart(text);
        return open < 0 ? text : text[..open] + item;
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

        var open = OpenStart(text[..caret]);
        if (open < 0)
            return (text, caret); // not inside a token — nothing to replace

        // Consume the token's tail after the caret up to its closing '}', unless another token starts
        // first (this one is unclosed) — then stop at the caret so we don't swallow the next token.
        var close = text.IndexOf('}', caret);
        var nextOpen = text.IndexOf('$', caret);
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
