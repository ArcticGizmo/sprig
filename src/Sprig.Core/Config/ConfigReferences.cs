using System.Text.RegularExpressions;

namespace Sprig.Core.Config;

/// <summary>
/// Extracts the <c>${sprig.&lt;path&gt;}</c> references a repo config makes, and works out which of
/// them are <b>stack variables</b> the stack must supply — i.e. refs that aren't the repo's own
/// <c>workspace</c>, <c>ports.*</c>, or cross-repo <c>provides.*</c>. Lets the UI pre-populate a
/// stack's variable editor with exactly what the chosen repos need.
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

    /// <summary>The stack-variable names this repo needs the stack to define.</summary>
    public static IReadOnlyList<string> RequiredStackVars(SprigRepoConfig config)
        => ReferencedPaths(config)
            .Where(IsStackVar)
            .Select(p => p) // the whole path is the var name (stack vars are simple names)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    static bool IsStackVar(string path)
        => path != "workspace"
           && !path.StartsWith("ports.", StringComparison.Ordinal)
           && !path.StartsWith("provides.", StringComparison.Ordinal);

    static IEnumerable<string> Templates(SprigRepoConfig config)
    {
        foreach (var env in config.Env)
            foreach (var v in env.Set.Values)
                yield return v;
        if (config.Compose is { } compose)
            foreach (var o in compose.Overrides)
                yield return o.Template;
        foreach (var v in config.Provides.Values)
            yield return v;
    }

    [GeneratedRegex(@"\$\{sprig\.([^}]+)\}")]
    private static partial Regex RefPattern();
}
