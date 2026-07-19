using System.Text;

namespace Sprig.Core.Substitution;

/// <summary>Thrown when a template cannot be fully resolved (unknown ref, cycle, or malformed syntax).</summary>
public sealed class SubstitutionException(string message) : Exception(message);

/// <summary>
/// Resolves <c>${sprig.&lt;path&gt;}</c> references in a template against an
/// <see cref="IVariableSource"/>. Values may reference other values; resolution follows the
/// dependency chain with cycle detection. Any unresolved reference is a hard error — we never
/// emit a partially-resolved string. Non-sprig <c>${...}</c> and bare <c>$</c> pass through
/// untouched (so shell-style vars in a compose/env file survive).
/// </summary>
public static class SubstitutionEngine
{
    const string Prefix = "${sprig.";

    /// <summary>Resolve every <c>${sprig...}</c> reference in <paramref name="input"/>.</summary>
    /// <exception cref="SubstitutionException">Unknown reference, cyclic reference, or malformed syntax.</exception>
    public static string Resolve(string input, IVariableSource source)
        => ResolveTemplate(input, source, new List<string>());

    static string ResolveTemplate(string input, IVariableSource source, List<string> chain)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var open = input.IndexOf("${", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(input, i, input.Length - i);
                break;
            }

            sb.Append(input, i, open - i);

            if (!IsPrefixAt(input, open))
            {
                // Not a sprig reference — emit "${" literally and continue scanning after it.
                sb.Append("${");
                i = open + 2;
                continue;
            }

            var keyStart = open + Prefix.Length;
            var close = input.IndexOf('}', keyStart);
            if (close < 0)
                throw new SubstitutionException($"unterminated '${{sprig.}}' reference in \"{input}\"");

            var key = input[keyStart..close].Trim();
            if (key.Length == 0)
                throw new SubstitutionException($"empty '${{sprig.}}' reference in \"{input}\"");

            sb.Append(ResolveKey(key, source, chain));
            i = close + 1;
        }

        return sb.ToString();
    }

    static string ResolveKey(string key, IVariableSource source, List<string> chain)
    {
        if (chain.Contains(key))
            throw new SubstitutionException(
                $"cyclic reference: {string.Join(" -> ", chain)} -> {key}");

        if (!source.TryResolve(key, out var raw))
            throw new SubstitutionException($"unknown reference '${{sprig.{key}}}'");

        chain.Add(key);
        try { return ResolveTemplate(raw, source, chain); }
        finally { chain.RemoveAt(chain.Count - 1); }
    }

    static bool IsPrefixAt(string s, int index)
        => index + Prefix.Length <= s.Length
           && string.CompareOrdinal(s, index, Prefix, 0, Prefix.Length) == 0;
}
