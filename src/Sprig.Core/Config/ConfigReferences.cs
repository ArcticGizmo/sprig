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
    /// <summary>The <c>${sprig.&lt;name&gt;}</c> names referenced by a single template string, in order
    /// (with duplicates) — used to colour an override red when it names an input that isn't declared.</summary>
    public static IEnumerable<string> ReferencedNames(string? template)
    {
        if (string.IsNullOrEmpty(template))
            yield break;
        foreach (Match m in RefPattern().Matches(template))
            yield return m.Groups[1].Value.Trim();
    }

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
        // Walk both the legacy flat surface (schema ≤2 / the editor's pre-modules shape) and the module
        // surface, so undeclared-input detection works whichever shape the config is in. Inputs are shared
        // at the repo level, so every module's templates are checked against the same declared set.
        foreach (var t in EnvComposeTemplates(config.Env ?? [], config.Compose ?? []))
            yield return t;
        foreach (var module in config.Modules)
            foreach (var t in EnvComposeTemplates(module.Env, module.Compose))
                yield return t;
    }

    static IEnumerable<string> EnvComposeTemplates(
        IReadOnlyList<EnvOverride> env, IReadOnlyList<ComposeConfig> compose)
    {
        foreach (var e in env)
            foreach (var v in e.Set.Values)
                yield return v;
        foreach (var c in compose)
            foreach (var o in c.Overrides)
                yield return o.Template;
    }

    [GeneratedRegex(@"\$\{sprig\.([^}]+)\}")]
    private static partial Regex RefPattern();
}
