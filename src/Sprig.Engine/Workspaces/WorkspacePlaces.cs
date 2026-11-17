using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;

namespace Sprig.Engine.Workspaces;

/// <summary>A folder of a workspace to go to.</summary>
/// <param name="Name"><c>root</c>; a repo's name; or a repo's <c>cd:</c> target, by its name alone when the workspace
/// has one repo, else as <c>repo/name</c>.</param>
/// <param name="What">What it is, in a few words, for a list to show beside the name.</param>
public sealed record WorkspacePlace(string Name, string Dir, string What);

/// <summary>Where in a workspace <c>sprig cd</c> (and the app's Open menu) can go: its root first, always, then each
/// repo when there's more than one, then the folders the repos name under <c>cd:</c>.</summary>
public static class WorkspacePlaces
{
    /// <param name="fallbackRoot">The root for a workspace that has never come up, so has no folders recorded: the
    /// repo's folder, or the orchestration's.</param>
    public static IReadOnlyList<WorkspacePlace> Of(WorkspaceRecord record, Plan? plan, string? fallbackRoot = null)
    {
        var places = new List<WorkspacePlace>();
        if (Root(record, fallbackRoot) is { } root) places.Add(new WorkspacePlace(DefinitionReader.CdRoot, root, "the workspace's folder"));
        var several = record.Repos.Count > 1;
        if (several)
            foreach (var repo in record.Repos)
                places.Add(new WorkspacePlace(repo.Name, repo.Dir, "repo"));
        foreach (var target in plan?.CdTargets ?? [])
        {
            if (record.Repos.FirstOrDefault(r => r.RepoPath == target.RepoPath) is not { } repo) continue;
            var dir = target.Path.Length == 0 ? repo.Dir : Path.Combine(repo.Dir, target.Path.Replace('/', Path.DirectorySeparatorChar));
            places.Add(new WorkspacePlace(several ? $"{repo.Name}/{target.Name}" : target.Name, dir, target.Path.Length == 0 ? repo.Name : target.Path));
        }
        return places;
    }

    /// <summary>The folder holding the workspace's worktrees (a lone repo's worktree is that folder); for one that runs
    /// in place, its repo, or the folder holding its repos.</summary>
    public static string? Root(WorkspaceRecord record, string? fallbackRoot = null)
    {
        if (record.Folder is { } folder) return folder;
        if (record.Repos.Count == 1) return record.Repos[0].Dir;
        if (record.Repos.Count > 1) return WorkspaceService.CommonParent(record.Repos.Select(r => r.Dir));
        return fallbackRoot;
    }

    /// <summary>The place called <paramref name="name"/> (any case), or null.</summary>
    public static WorkspacePlace? Find(IReadOnlyList<WorkspacePlace> places, string name)
        => places.FirstOrDefault(p => p.Name == name) ?? places.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
