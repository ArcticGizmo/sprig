using System.Text.RegularExpressions;
using Sprig.Core.Store;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a stack can't be defined, found, or imported.</summary>
public sealed class StackException(string message) : Exception(message);

/// <summary>Persists <see cref="StackDefinition"/>s in the central store and handles export/import.</summary>
public sealed partial class StackStore(ISprigPaths paths, RepoRegistryStore registry, InstanceStore instances)
{
    public void Save(StackDefinition stack)
    {
        Validate(stack);

        // A stack that live workspaces were built from is frozen: changing its wiring wouldn't touch
        // those already-materialised workspaces, so it would only mislead. Creating a new stack, or
        // re-saving one nothing depends on, is fine. (The desktop app also gates Edit on this.)
        if (File.Exists(FilePath(stack.Name)))
        {
            var users = instances.LoadAll().Count(i => i.Stack == stack.Name);
            if (users > 0)
                throw new StackException(
                    $"can't modify stack '{stack.Name}': {users} workspace{(users == 1 ? "" : "s")} " +
                    "were created from it — remove them first");
        }

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

        // Stacks reference repos by name, so an imported stack only saves once every repo it names is
        // registered on this machine. Report all the missing ones at once, so the fix is a single pass.
        var unknown = stack.Repos.Where(repo => registry.Get(repo) is null).ToList();
        if (unknown.Count > 0)
            throw new StackException(
                $"stack '{stack.Name}' references unregistered repo{(unknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unknown.Select(r => $"'{r}'"))} — register {(unknown.Count == 1 ? "it" : "them")} first");
    }

    string FilePath(string name) => Path.Combine(paths.StacksDir, name + ".json");

    // Stack names allow '+' (e.g. "web+api"); they are filenames, not git branches.
    [GeneratedRegex(@"^[A-Za-z0-9._+-]+$")]
    private static partial Regex NamePattern();
}
