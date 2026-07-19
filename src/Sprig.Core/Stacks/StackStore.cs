using System.Text.RegularExpressions;
using Sprig.Core.Store;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a stack can't be defined, found, or imported.</summary>
public sealed class StackException(string message) : Exception(message);

/// <summary>Persists <see cref="StackDefinition"/>s in the central store and handles export/import.</summary>
public sealed partial class StackStore(ISprigPaths paths, RepoRegistryStore registry)
{
    public void Save(StackDefinition stack)
    {
        Validate(stack);
        JsonFile.Write(FilePath(stack.Name), stack);
    }

    public StackDefinition? Get(string name) => JsonFile.Read<StackDefinition>(FilePath(name));

    public IReadOnlyList<StackDefinition> List()
    {
        if (!Directory.Exists(paths.StacksDir)) return [];
        return Directory.EnumerateFiles(paths.StacksDir, "*.json")
            .Select(f => JsonFile.Read<StackDefinition>(f))
            .OfType<StackDefinition>()
            .OrderBy(s => s.Name)
            .ToList();
    }

    public void Remove(string name)
    {
        var file = FilePath(name);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Copy a stack's JSON out for sharing; returns the destination path.</summary>
    public string Export(string name, string destPath)
    {
        var stack = Get(name) ?? throw new StackException($"unknown stack '{name}'");
        JsonFile.Write(destPath, stack);
        return destPath;
    }

    /// <summary>Read a stack JSON from a file, validate against the registry, and save it.</summary>
    public StackDefinition Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new StackException($"stack file not found: {sourcePath}");
        var stack = JsonFile.Read<StackDefinition>(sourcePath)
            ?? throw new StackException($"could not read stack from {sourcePath}");
        Save(stack);
        return stack;
    }

    void Validate(StackDefinition stack)
    {
        if (string.IsNullOrWhiteSpace(stack.Name) || !NamePattern().IsMatch(stack.Name))
            throw new StackException($"invalid stack name '{stack.Name}' (use letters, digits, '.', '-', '_')");
        if (stack.Repos.Count == 0)
            throw new StackException("a stack must reference at least one repo");
        foreach (var repo in stack.Repos)
            if (registry.Get(repo) is null)
                throw new StackException($"stack '{stack.Name}' references unknown repo '{repo}' (register it first)");
    }

    string FilePath(string name) => Path.Combine(paths.StacksDir, name + ".json");

    // Stack names allow '+' (e.g. "web+api"); they are filenames, not git branches.
    [GeneratedRegex(@"^[A-Za-z0-9._+-]+$")]
    private static partial Regex NamePattern();
}
