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

    /// <summary>
    /// References that resolve to nothing the repo declares — i.e. mistakes. A reference is accepted when it
    /// is <c>workspace</c>, a stack-era declared <see cref="InputDeclaration"/> (dotless), or a map-era
    /// <c>&lt;capability&gt;.&lt;output&gt;</c>: a self/local-provided output (checked exactly) or a needed
    /// capability / alias (the output is only knowable at map-resolve time, so the head alone must match).
    /// </summary>
    public static IReadOnlyList<string> UndeclaredReferences(SprigRepoConfig config)
    {
        var inputs = config.Inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var (provided, needed) = CapabilitySurface(config);
        var bad = new List<string>();
        foreach (var p in ReferencedPaths(config))
        {
            if (p == "workspace")
                continue;
            var dot = p.IndexOf('.');
            if (dot < 0)
            {
                if (!inputs.Contains(p))            // stack-era input, or an unknown bare name
                    bad.Add(p);
                continue;
            }
            var head = p[..dot];
            var tail = p[(dot + 1)..];
            if (provided.TryGetValue(head, out var outs) && outs.Contains(tail))
                continue;                            // self/local-provided output — exact match
            if (needed.Contains(head))
                continue;                            // wired need/alias — output validated at resolve time
            bad.Add(p);
        }
        return bad;
    }

    /// <summary>The repo's map-model surface: provided capabilities → their output names, and the set of
    /// needed capability names + aliases. Gathered across every module (repo-global, as inputs are).</summary>
    static (Dictionary<string, HashSet<string>> Provided, HashSet<string> Needed) CapabilitySurface(SprigRepoConfig config)
    {
        var provided = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in config.EffectiveModules)
        {
            foreach (var p in module.Provides)
            {
                if (!provided.TryGetValue(p.Capability, out var outs))
                    provided[p.Capability] = outs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in p.Outputs.Keys)
                    outs.Add(o);
            }
            foreach (var n in module.Needs)
            {
                needed.Add(n.Capability);
                needed.Add(n.Alias);
            }
        }
        return (provided, needed);
    }

    static IEnumerable<string> Templates(SprigRepoConfig config)
    {
        // Walk both the legacy flat surface (schema ≤2 / the editor's pre-modules shape) and the module
        // surface, so undeclared-reference detection works whichever shape the config is in.
        foreach (var t in EnvComposeTemplates(config.Env ?? [], config.Compose ?? []))
            yield return t;
        foreach (var module in config.Modules)
            foreach (var t in EnvComposeTemplates(module.Env, module.Compose))
                yield return t;
        // Map-model: provided outputs' derived templates (EffectiveModules unifies top-level sugar + modules).
        foreach (var module in config.EffectiveModules)
            foreach (var cap in module.Provides)
                foreach (var o in cap.Outputs.Values)
                    if (!o.IsPort && !string.IsNullOrEmpty(o.Template))
                        yield return o.Template!;
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
