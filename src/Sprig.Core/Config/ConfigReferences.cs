using System.Text.RegularExpressions;

namespace Sprig.Core.Config;

/// <summary>
/// Inspects the <c>${sprig.&lt;path&gt;}</c> references a repo's templates make. A repo may reference
/// <c>workspace</c>, its own self-provided <c>&lt;capability&gt;.&lt;output&gt;</c>, or a declared need
/// (whose output resolves at map time); anything else is a mistake the validator flags.
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
    /// is <c>workspace</c> or a <c>&lt;capability&gt;.&lt;output&gt;</c>: a self/local-provided output (checked
    /// exactly) or a needed capability / alias (the output is only knowable at map-resolve time, so the head
    /// alone must match).
    /// </summary>
    public static IReadOnlyList<string> UndeclaredReferences(SprigRepoConfig config)
    {
        var (exact, open) = ReferenceScope(config);
        return ReferencedPaths(config).Where(p => !IsReferenceKnown(p, exact, open)).ToList();
    }

    /// <summary>
    /// Whether a single <c>${sprig.&lt;reference&gt;}</c> path is accepted, given a repo's
    /// <paramref name="exactNames"/> (the names matched verbatim — <c>workspace</c> and each
    /// self-provided <c>&lt;capability&gt;.&lt;output&gt;</c>) and its <paramref name="openCapabilities"/> (needed
    /// capability names + aliases, whose output is only knowable at map-resolve time so any tail is accepted).
    /// The per-reference rule behind <see cref="UndeclaredReferences"/>, exposed so the live editor's token
    /// colouring matches exactly what Save validates.
    /// </summary>
    public static bool IsReferenceKnown(string reference, ISet<string> exactNames, ISet<string> openCapabilities)
    {
        if (exactNames.Contains(reference)) return true;   // workspace / input / self-provided <cap>.<out>
        var dot = reference.IndexOf('.');
        return dot > 0 && openCapabilities.Contains(reference[..dot]);  // needed cap/alias — any output
    }

    /// <summary>The two reference sets a repo accepts: verbatim <paramref name="exact"/> names (workspace +
    /// inputs + self-provided <c>&lt;cap&gt;.&lt;out&gt;</c>) and <paramref name="open"/> capability heads
    /// (needs/aliases). Feeds <see cref="IsReferenceKnown"/>.</summary>
    static (HashSet<string> Exact, HashSet<string> Open) ReferenceScope(SprigRepoConfig config)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal) { "workspace" };
        var (provided, needed) = CapabilitySurface(config);
        foreach (var (cap, outs) in provided)
            foreach (var o in outs)
                exact.Add($"{cap}.{o}");
        return (exact, needed);
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
                foreach (var o in p.OutputNames)
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
        // Map-model: provided capabilities' derived-shape templates (EffectiveModules unifies top-level
        // sugar + modules). Ports own no template — only shapes reference other outputs.
        foreach (var module in config.EffectiveModules)
            foreach (var cap in module.Provides)
                foreach (var template in cap.Shapes.Values)
                    if (!string.IsNullOrEmpty(template))
                        yield return template;
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
