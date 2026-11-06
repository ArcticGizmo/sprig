using Sprig.Engine.Definitions;

namespace Sprig.Cli;

/// <summary>What the current folder is to sprig, so commands run from a repo needn't name it.</summary>
static class Here
{
    /// <summary>
    /// The ID and, inside one of a workspace's worktrees, the workspace the current folder belongs to: the nearest
    /// folder up from here that's a worktree sprig made, or that's on the Repos list.
    /// </summary>
    public static (string Id, string? Workspace) Find(CliContext cli)
    {
        var stored = new StoredDefinitions(cli.Paths);
        var worktrees = cli.Workspaces.List()
            .SelectMany(r => r.Repos.Where(x => x.Worktree).Select(x => (record: r, dir: x.Dir)))
            .ToArray();
        var repos = stored.Repos();

        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            if (worktrees.FirstOrDefault(w => SamePath(w.dir, dir.FullName)) is { record: { } record })
                return (record.Orchestration, record.Name);

            var here = repos.Where(e => SamePath(e.Folder, dir.FullName)).ToArray();
            if (here.Length == 1) return (here[0].Id, null);
            if (here.Length > 1)
                throw new InvalidOperationException(
                    $"{dir.FullName} is on the Repos list as {string.Join(" and ", here.Select(e => $"'{e.Id}'"))}; name the one you mean, e.g. sprig up {here[0].Id}");
        }
        throw new InvalidOperationException("this folder isn't on the Repos list. Run sprig init here, or name a repo or orchestration ID");
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
