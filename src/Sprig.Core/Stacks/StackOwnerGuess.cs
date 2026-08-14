using System.Text;

namespace Sprig.Core.Stacks;

/// <summary>
/// Guesses which repo owns (produces) a stack port from names alone — the assist behind the repo
/// graph's "Guess owners" action. The convention it leans on is the common one: a port is named after
/// the repo that serves it (<c>api_port</c> → <c>api</c>, <c>postgresPort</c> → <c>postgres</c>). It is
/// deliberately conservative — it proposes only a single, unambiguous match and only for ports that
/// have no owner yet, so it fills the blanks without ever overriding a choice the author made or
/// guessing when two repos are equally plausible. The result is a proposal the user reviews on the
/// graph, not an authority: ownership stays an explicit, viz-only overlay (<see cref="StackDefinition.Owners"/>).
/// </summary>
public static class StackOwnerGuess
{
    /// <summary>
    /// Propose an owning repo for each declared port that isn't already owned, inferred from the port's
    /// name. A repo matches when its name is a token of the port name (<c>api_port</c>, <c>apiPort</c>,
    /// <c>api-port</c> all tokenise to include <c>api</c>) or the port name begins with it. The longest
    /// matching repo name wins; an exact tie between two repos is left unproposed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Guess(
        IReadOnlyList<string> repos,
        IReadOnlyList<string> ports,
        IReadOnlyDictionary<string, string> existingOwners)
    {
        var proposals = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var port in ports)
        {
            if (existingOwners.ContainsKey(port)) continue; // never override an explicit pick

            var tokens = Tokenize(port);
            var portAlnum = Alnum(port);
            string? best = null;
            var bestLen = 0;
            var tie = false;

            foreach (var repo in repos)
            {
                if (!Matches(tokens, portAlnum, repo)) continue;
                var len = Alnum(repo).Length;
                if (len > bestLen) { best = repo; bestLen = len; tie = false; }
                else if (len == bestLen && !string.Equals(repo, best, StringComparison.OrdinalIgnoreCase)) tie = true;
            }

            if (best is not null && !tie) proposals[port] = best;
        }

        return proposals;
    }

    /// <summary>A repo name matches when it is one of the port's tokens, or a (≥2-char) leading run of it.</summary>
    static bool Matches(IReadOnlyList<string> portTokens, string portAlnum, string repo)
    {
        var r = Alnum(repo);
        if (r.Length == 0) return false;
        if (portTokens.Contains(r)) return true;
        return r.Length >= 2 && portAlnum.StartsWith(r, StringComparison.Ordinal);
    }

    /// <summary>Lower-case tokens split on non-alphanumerics and camelCase boundaries (<c>apiPort</c> → api, port).</summary>
    static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();

        void Flush()
        {
            if (cur.Length == 0) return;
            tokens.Add(cur.ToString().ToLowerInvariant());
            cur.Clear();
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) Flush(); // camelCase edge
            cur.Append(c);
        }
        Flush();
        return tokens;
    }

    static string Alnum(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
