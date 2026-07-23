using System.Text.RegularExpressions;

namespace Sprig.Core.Config;

/// <summary>
/// Inspects the <c>${sprig.&lt;path&gt;}</c> references a repo's templates make. A repo may only
/// reference its own declared <see cref="InputDeclaration"/>s and <c>workspace</c>; anything else
/// is a mistake the validator flags (repos are pure consumers — they don't know about stack
/// ports or other repos; the stack supplies each input via bindings).
/// </summary>
public static partial class ConfigReferences
{
    /// <summary>Every distinct <c>${sprig.&lt;path&gt;}</c> path referenced by the config's templates.</summary>
    public static IReadOnlyList<string> ReferencedPaths(SprigRepoConfig config)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in Templates(config))
            foreach (Match m in RefPattern().Matches(template))
                found.Add(m.Groups[1].Value.Trim());
        return found.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>References that are neither a declared input nor <c>workspace</c> — i.e. mistakes.</summary>
    public static IReadOnlyList<string> UndeclaredReferences(SprigRepoConfig config)
    {
        var declared = config.Inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        return ReferencedPaths(config)
            .Where(p => p != "workspace" && !declared.Contains(p))
            .ToList();
    }

    static IEnumerable<string> Templates(SprigRepoConfig config)
    {
        foreach (var env in config.Env)
            foreach (var v in env.Set.Values)
                yield return v;
        foreach (var compose in config.Compose)
            foreach (var o in compose.Overrides)
                yield return o.Template;
    }

    [GeneratedRegex(@"\$\{sprig\.([^}]+)\}")]
    private static partial Regex RefPattern();
}
