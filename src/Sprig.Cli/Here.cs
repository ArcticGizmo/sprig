using Sprig.Engine.Definitions;
using Sprig.Engine.Workspaces;

namespace Sprig.Cli;

/// <summary>What the current folder is to sprig, so commands run from a repo needn't name it.</summary>
static class Here
{
    /// <summary>
    /// The ID and, inside a workspace's folder, the workspace the current folder belongs to: the nearest folder up from
    /// here that's a worktree sprig made, a workspace's folder of worktrees, or a folder on the Repos list.
    /// </summary>
    public static (string Id, string? Workspace) Find(CliContext cli)
        => TryFind(cli) ?? throw NotHere();

    /// <summary>
    /// <see cref="Find(CliContext)"/> for a command that names its workspace with <c>-w</c>: outside every repo, that
    /// name is enough when just one repo or orchestration has a workspace called it.
    /// </summary>
    public static (string Id, string? Workspace) Find(CliContext cli, string? workspace)
        => TryFind(cli)
           ?? (workspace is not null && Named(cli, workspace) is { } existing ? (existing.Orchestration, existing.Name) : throw NotHere());

    /// <summary>As <see cref="Find(CliContext)"/>, but null outside anything sprig knows.</summary>
    public static (string Id, string? Workspace)? TryFind(CliContext cli)
        => TryFind(Environment.CurrentDirectory, cli.Workspaces.List(), new StoredDefinitions(cli.Paths).Repos());

    /// <summary>
    /// The one workspace called <paramref name="name"/>, of whichever repo or orchestration has it, for a command run
    /// outside every repo with just a workspace's name. Null when none has it.
    /// </summary>
    public static WorkspaceRecord? Named(CliContext cli, string name) => Named(cli.Workspaces.List(), name);

    internal static WorkspaceRecord? Named(IEnumerable<WorkspaceRecord> workspaces, string name)
    {
        var matches = workspaces.Where(r => r.Name == name).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"{string.Join(" and ", matches.Select(r => $"'{r.Orchestration}'"))} each have a workspace called '{name}'; " +
                $"put the ID first, e.g. {matches[0].Orchestration} {name}"),
        };
    }

    internal static (string Id, string? Workspace) Find(string currentDirectory, IEnumerable<WorkspaceRecord> workspaces, IEnumerable<RepoEntry> repoList)
        => TryFind(currentDirectory, workspaces, repoList) ?? throw NotHere();

    public static InvalidOperationException NotHere()
        => new("this folder isn't on the Repos list. Run sprig init here, or name a repo or orchestration ID");

    internal static (string Id, string? Workspace)? TryFind(string currentDirectory, IEnumerable<WorkspaceRecord> workspaces, IEnumerable<RepoEntry> repoList)
    {
        var records = workspaces.ToArray();
        var worktrees = records
            .SelectMany(r => r.Repos.Where(x => x.Worktree).Select(x => (record: r, dir: x.Dir)))
            .ToArray();
        var repos = repoList.ToArray();

        for (var dir = new DirectoryInfo(currentDirectory); dir is not null; dir = dir.Parent)
        {
            if (worktrees.FirstOrDefault(w => SamePath(w.dir, dir.FullName)) is { record: { } record })
                return (record.Orchestration, record.Name);
            // The folder that holds a workspace's worktrees, such as shop-web--feat-x around shop-web, is where its
            // name sends people first.
            if (records.FirstOrDefault(r => r.Folder is { } folder && SamePath(folder, dir.FullName)) is { } holder)
                return (holder.Orchestration, holder.Name);

            var here = repos.Where(e => SamePath(e.Folder, dir.FullName)).ToArray();
            if (here.Length == 1) return (here[0].Id, null);
            if (here.Length > 1)
                throw new InvalidOperationException(
                    $"{dir.FullName} is on the Repos list as {string.Join(" and ", here.Select(e => $"'{e.Id}'"))}; name the one you mean, e.g. sprig up {here[0].Id}");
        }
        return null;
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
