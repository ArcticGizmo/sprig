using System.Text.RegularExpressions;
using Sprig.Core.Config;
using Sprig.Core.Store;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a repo can't be registered or looked up.</summary>
public sealed class RepoRegistryException(string message) : Exception(message);

/// <summary>A repo known to sprig: a logical name and the absolute path to its working tree.</summary>
/// <param name="VirtualConfigPath">Set for a <b>virtual</b> repo — one whose config sprig holds instead of the
/// repo's committed <c>.sprig.json</c>, so a config can be tried out before it's merged, and one checkout can
/// be registered several times under different names and configs.</param>
public sealed record RegisteredRepo(string Name, string Path, string? VirtualConfigPath = null)
{
    public bool IsVirtual => VirtualConfigPath is not null;

    /// <summary>The config this registration runs on: sprig's copy for a virtual repo, else the repo's own.</summary>
    public string ConfigPath => VirtualConfigPath ?? System.IO.Path.Combine(Path, WorkspaceConfigFileName);

    internal const string WorkspaceConfigFileName = ".sprig.json";
}

/// <summary>
/// The machine-local known-repos registry (central <c>repos.json</c>). Stacks reference repos by
/// name, so a stack definition stays portable while the concrete paths stay machine-local. A virtual
/// repo's config sits beside it in <see cref="ISprigPaths.RepoConfigsDir"/>, keyed by the same name.
/// </summary>
public sealed partial class RepoRegistryStore(ISprigPaths paths)
{
    public RegisteredRepo Add(string repoPath, string? name = null)
    {
        if (!Directory.Exists(repoPath))
            throw new RepoRegistryException($"path does not exist: {repoPath}");
        var full = Path.GetFullPath(repoPath).TrimEnd('\\', '/');

        var configPath = Path.Combine(full, RegisteredRepo.WorkspaceConfigFileName);
        if (!File.Exists(configPath))
            throw new RepoRegistryException($"not a sprig repo (no {RegisteredRepo.WorkspaceConfigFileName}): {full}");

        var config = SprigConfigLoader.LoadFromFile(configPath);
        var resolvedName = string.IsNullOrWhiteSpace(name) ? config.Name : name!;
        if (string.IsNullOrWhiteSpace(resolvedName))
            throw new RepoRegistryException("could not determine a repo name (pass --name or set 'name' in .sprig.json)");
        if (File.Exists(paths.RepoConfigFile(resolvedName)))
            throw new RepoRegistryException($"'{resolvedName}' is a virtual repo — register this one under another name");

        var data = Load();
        if (data.Repos.TryGetValue(resolvedName, out var existing) &&
            !string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
            throw new RepoRegistryException($"a different repo is already registered as '{resolvedName}' ({existing})");

        data.Repos[resolvedName] = full;
        Save(data);
        return new RegisteredRepo(resolvedName, full);
    }

    /// <summary>
    /// Register <paramref name="repoPath"/> as a virtual repo: <paramref name="config"/> is kept in sprig's
    /// store under <paramref name="name"/> and nothing is written to the repo. The config's own name is set to
    /// <paramref name="name"/>, since that's the key maps and workspaces refer to it by.
    /// </summary>
    public RegisteredRepo AddVirtual(string repoPath, string name, SprigRepoConfig config)
    {
        if (!Directory.Exists(repoPath))
            throw new RepoRegistryException($"path does not exist: {repoPath}");
        var full = Path.GetFullPath(repoPath).TrimEnd('\\', '/');

        name = (name ?? "").Trim();
        if (name.Length == 0)
            throw new RepoRegistryException("a virtual repo needs a name");
        if (name is "." or ".." || !NamePattern().IsMatch(name))
            throw new RepoRegistryException($"invalid repo name '{name}' (use letters, digits, '.', '-', '_')");

        var data = Load();
        if (data.Repos.TryGetValue(name, out var existing))
            throw new RepoRegistryException($"'{name}' is already registered ({existing})");

        var file = paths.RepoConfigFile(name);
        SprigConfigWriter.Write(config with { Name = name }, file);
        data.Repos[name] = full;
        Save(data);
        return new RegisteredRepo(name, full, file);
    }

    /// <summary>Unregister a repo. A virtual repo's config goes with it — it has no other home.</summary>
    public void Remove(string name)
    {
        var data = Load();
        if (data.Repos.Remove(name)) Save(data);
        var file = paths.RepoConfigFile(name);
        if (File.Exists(file)) File.Delete(file);
    }

    public RegisteredRepo? Get(string name)
        => Load().Repos.TryGetValue(name, out var path) ? Registered(name, path) : null;

    public IReadOnlyList<RegisteredRepo> List()
        => Load().Repos.Select(kv => Registered(kv.Key, kv.Value))
                       .OrderBy(r => r.Name)
                       .ToList();

    RegisteredRepo Registered(string name, string path)
    {
        var file = paths.RepoConfigFile(name);
        return new RegisteredRepo(name, path, File.Exists(file) ? file : null);
    }

    RegistryData Load() => JsonFile.Read<RegistryData>(paths.ReposFile) ?? new RegistryData();
    void Save(RegistryData data) => JsonFile.Write(paths.ReposFile, data);

    // Doubles as the virtual config's file name, so it has to stay filename-safe.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();

    sealed class RegistryData
    {
        public Dictionary<string, string> Repos { get; init; } = new();
    }
}
