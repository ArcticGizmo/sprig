using Sprig.Core.Config;
using Sprig.Core.Store;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a repo can't be registered or looked up.</summary>
public sealed class RepoRegistryException(string message) : Exception(message);

/// <summary>A repo known to sprig: a logical name and the absolute path to its working tree.</summary>
public sealed record RegisteredRepo(string Name, string Path);

/// <summary>
/// The machine-local known-repos registry (central <c>repos.json</c>). Stacks reference repos by
/// name, so a stack definition stays portable while the concrete paths stay machine-local.
/// </summary>
public sealed class RepoRegistryStore(ISprigPaths paths)
{
    public RegisteredRepo Add(string repoPath, string? name = null)
    {
        if (!Directory.Exists(repoPath))
            throw new RepoRegistryException($"path does not exist: {repoPath}");
        var full = Path.GetFullPath(repoPath).TrimEnd('\\', '/');

        var configPath = Path.Combine(full, WorkspaceConfigFileName);
        if (!File.Exists(configPath))
            throw new RepoRegistryException($"not a sprig repo (no {WorkspaceConfigFileName}): {full}");

        var config = SprigConfigLoader.LoadFromFile(configPath);
        var resolvedName = string.IsNullOrWhiteSpace(name) ? config.Name : name!;
        if (string.IsNullOrWhiteSpace(resolvedName))
            throw new RepoRegistryException("could not determine a repo name (pass --name or set 'name' in .sprig.json)");

        var data = Load();
        if (data.Repos.TryGetValue(resolvedName, out var existing) &&
            !string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
            throw new RepoRegistryException($"a different repo is already registered as '{resolvedName}' ({existing})");

        data.Repos[resolvedName] = full;
        Save(data);
        return new RegisteredRepo(resolvedName, full);
    }

    public void Remove(string name)
    {
        var data = Load();
        if (data.Repos.Remove(name)) Save(data);
    }

    public RegisteredRepo? Get(string name)
        => Load().Repos.TryGetValue(name, out var path) ? new RegisteredRepo(name, path) : null;

    public IReadOnlyList<RegisteredRepo> List()
        => Load().Repos.Select(kv => new RegisteredRepo(kv.Key, kv.Value))
                       .OrderBy(r => r.Name)
                       .ToList();

    const string WorkspaceConfigFileName = ".sprig.json";

    RegistryData Load() => JsonFile.Read<RegistryData>(paths.ReposFile) ?? new RegistryData();
    void Save(RegistryData data) => JsonFile.Write(paths.ReposFile, data);

    sealed class RegistryData
    {
        public Dictionary<string, string> Repos { get; init; } = new();
    }
}
