using System.Text.RegularExpressions;
using Sprig.Core.Store;

namespace Sprig.Core.Shared;

/// <summary>Thrown when a shared resource can't be defined, found, or applied.</summary>
public sealed class SharedResourceException(string message) : Exception(message);

/// <summary>
/// Persists <see cref="SharedResourceDefinition"/>s in the central store's <c>shared/</c> directory.
/// Machine-local by design — these are never written into a repo and are not exported with a stack, which
/// is the property that keeps a pooling decision from leaking onto a teammate who didn't make it.
/// </summary>
public sealed partial class SharedResourceStore(ISprigPaths paths)
{
    public const int SupportedSchema = 1;

    public void Save(SharedResourceDefinition resource)
    {
        var issues = Validate(resource);
        if (issues.Count > 0)
            throw new SharedResourceException(
                $"invalid shared resource '{resource.Name}':\n  " + string.Join("\n  ", issues));

        JsonFile.Write(FilePath(resource.Name), resource);
    }

    public SharedResourceDefinition? Get(string name) => JsonFile.Read<SharedResourceDefinition>(FilePath(name));

    /// <summary>Every defined resource, enabled or not, by name.</summary>
    public IReadOnlyList<SharedResourceDefinition> List()
    {
        if (!Directory.Exists(paths.SharedDir)) return [];
        return [.. Directory.EnumerateFiles(paths.SharedDir, "*.json")
            .Select(JsonFile.Read<SharedResourceDefinition>)
            .OfType<SharedResourceDefinition>()
            .OrderBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>The resources that should take part in a plan right now — the enabled ones.</summary>
    public IReadOnlyList<SharedResourceDefinition> Active() => [.. List().Where(r => r.Enabled)];

    public void Remove(string name)
    {
        var file = FilePath(name);
        if (File.Exists(file)) File.Delete(file);
    }

    public string FilePath(string name) => Path.Combine(paths.SharedDir, $"{name}.json");

    /// <summary>Everything wrong with a definition, in the order a reader would want to fix it.</summary>
    public static IReadOnlyList<string> Validate(SharedResourceDefinition resource)
    {
        var issues = new List<string>();

        if (resource.Schema != SupportedSchema)
            issues.Add($"unsupported schema {resource.Schema} (expected {SupportedSchema})");
        if (string.IsNullOrWhiteSpace(resource.Name) || !NamePattern().IsMatch(resource.Name))
            issues.Add($"invalid name '{resource.Name}' (use letters, digits, '.', '-', '_', '+')");
        if (resource.Capacity < 1)
            issues.Add($"capacity must be at least 1 (got {resource.Capacity})");
        if (resource.WhenIdle is not ("stop" or "keep"))
            issues.Add($"whenIdle must be 'stop' or 'keep' (got '{resource.WhenIdle}')");
        foreach (var key in resource.Unknown.Keys)
            issues.Add($"unknown key '{key}'");

        if (resource.Injects.Count == 0)
            issues.Add("injects[] is empty — the resource would never apply to anything");

        var seenRepos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inject in resource.Injects)
        {
            if (string.IsNullOrWhiteSpace(inject.Repo))
            {
                issues.Add("an injects[] entry has no repo");
                continue;
            }
            if (!seenRepos.Add(inject.Repo))
                issues.Add($"injects[] targets repo '{inject.Repo}' more than once — merge them into one entry");
            if (inject.Inputs.Count == 0 && inject.Env.Count == 0
                && inject.Compose.Count == 0 && inject.Suppress.Count == 0)
                issues.Add($"injects[] entry for '{inject.Repo}' changes nothing");

            foreach (var key in inject.Unknown.Keys)
                issues.Add($"unknown key '{key}' in injects[{inject.Repo}]");
            foreach (var env in inject.Env)
            {
                if (string.IsNullOrWhiteSpace(env.File))
                    issues.Add($"an env override for '{inject.Repo}' has no file");
                if (env.Set.Count == 0)
                    issues.Add($"env override '{env.File}' for '{inject.Repo}' sets no keys");
                foreach (var key in env.Unknown.Keys)
                    issues.Add($"unknown key '{key}' in injects[{inject.Repo}].env[{env.File}]");
            }
            foreach (var compose in inject.Compose)
            {
                if (string.IsNullOrWhiteSpace(compose.File))
                    issues.Add($"a compose override for '{inject.Repo}' has no file");
                foreach (var over in compose.Overrides)
                    if (over.Path.Count == 0)
                        issues.Add($"a compose override for '{inject.Repo}' has an empty path");
                foreach (var key in compose.Unknown.Keys)
                    issues.Add($"unknown key '{key}' in injects[{inject.Repo}].compose[{compose.File}]");
            }
            foreach (var suppress in inject.Suppress)
            {
                if (string.IsNullOrWhiteSpace(suppress.File))
                    issues.Add($"a suppress entry for '{inject.Repo}' has no file");
                if (suppress.Services.Count == 0)
                    issues.Add($"suppress entry '{suppress.File}' for '{inject.Repo}' names no services");
                foreach (var key in suppress.Unknown.Keys)
                    issues.Add($"unknown key '{key}' in injects[{inject.Repo}].suppress[{suppress.File}]");
            }
        }

        return issues;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._+-]+$")]
    private static partial Regex NamePattern();
}
