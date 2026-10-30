using Sprig.Core.Config;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>Where a directory sits within Sprig's workspaces: the workspace, the repo whose worktree holds
/// it, and the module (by name) whose directory holds it, or null at a repo root with no module there.</summary>
public sealed record WorkspaceLocation(InstanceRecord Record, InstanceRepo Repo, string? Module);

/// <summary>
/// A workspace's <b>purpose</b> layer — the label, note and tags its claimer owns and can change at any time — and
/// locating a directory within the workspaces. Neither touches git or infra: purpose is metadata about the
/// work, and git state is read live rather than stored.
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>Set (or, with null/blank, clear) a workspace's label.</summary>
    public InstanceRecord SetLabel(string workspace, string? label)
        => Update(workspace, r => r with { Label = Clean(label) });

    /// <summary>Set (or, with null/blank, clear) a workspace's status note.</summary>
    public InstanceRecord SetNote(string workspace, string? note)
        => Update(workspace, r => r with { Note = Clean(note) });

    /// <summary>Set the slot's colour (<c>#rrggbb</c>), or with null go back to its palette colour.</summary>
    public InstanceRecord SetColour(string workspace, string? colour)
    {
        var value = Clean(colour);
        if (value is not null && !WorkspaceColour.IsValid(value))
            throw new WorkspaceException($"'{value}' is not a colour — use #rrggbb (e.g. #2563eb)");
        return Update(workspace, r => r with { Colour = value?.ToLowerInvariant() });
    }

    /// <summary>
    /// Write a VS Code <c>.code-workspace</c> for the workspace into the central store (never the repo) and
    /// return its path. It opens every repo's worktree as a folder, titles the window with what the workspace
    /// is for, and tints the title bar with the slot's colour, so several open workspaces can be told apart.
    /// Rewritten on every call, so it always carries the current label.
    /// </summary>
    public string WriteCodeWorkspace(InstanceRecord record)
    {
        var colour = WorkspaceColour.For(record);
        var foreground = WorkspaceColour.Foreground(colour);
        var headline = !string.IsNullOrWhiteSpace(record.Label) ? record.Label
            : record.Repos.Select(r => Git.WorktreeHead.Read(r.WorktreePath)?.Display).FirstOrDefault(b => b is not null);
        var title = headline is null ? record.Workspace : $"{headline} · {record.Workspace}";

        var document = new
        {
            folders = record.Repos.Select(r => new { name = r.Name, path = r.WorktreePath }),
            settings = new Dictionary<string, object>
            {
                ["window.title"] = title + "${separator}${activeEditorShort}",
                ["workbench.colorCustomizations"] = new Dictionary<string, string>
                {
                    ["titleBar.activeBackground"] = colour,
                    ["titleBar.activeForeground"] = foreground,
                    ["titleBar.inactiveBackground"] = colour + "cc",
                    ["titleBar.inactiveForeground"] = foreground + "99",
                },
            },
        };

        var path = Path.Combine(paths.InstanceDir(record.Workspace), $"{record.Workspace}.code-workspace");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Add or overwrite tags, then remove <paramref name="remove"/> keys. Every key is validated
    /// before anything is written.</summary>
    public InstanceRecord Tag(string workspace, IReadOnlyDictionary<string, string> set, IEnumerable<string>? remove = null)
    {
        foreach (var key in set.Keys) WorkspaceTags.EnsureValidKey(key);
        var drop = (remove ?? []).Select(WorkspaceTags.EnsureValidKey).ToList();
        return Update(workspace, r =>
        {
            var tags = new Dictionary<string, string>(r.Tags, StringComparer.Ordinal);
            foreach (var (k, v) in set) tags[k] = v;
            foreach (var k in drop) tags.Remove(k);
            return r with { Tags = tags };
        });
    }

    /// <summary>Every workspace matching all of <paramref name="filters"/>, ordered by name.</summary>
    public IReadOnlyList<InstanceRecord> List(IReadOnlyList<WorkspaceTags.Filter> filters)
        => instances.LoadAll()
            .Where(r => WorkspaceTags.MatchesAll(r, filters))
            .OrderBy(r => r.Workspace, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Resolve a directory to the workspace, repo and module that contain it by matching it against the
    /// recorded worktree paths — no marker file in the worktree. The deepest match wins, so a worktree nested
    /// under another path still resolves to itself. A directory reached through a worktree's link counts too,
    /// since a shell keeps the path it was given rather than resolving the junction. Null when the directory is
    /// in no workspace.
    /// </summary>
    public WorkspaceLocation? Locate(string directory)
    {
        var dir = Normalise(directory);
        WorkspaceLocation? best = null;
        var bestLength = -1;
        foreach (var record in instances.LoadAll())
            foreach (var repo in record.Repos)
                foreach (var path in new[] { repo.WorktreePath, repo.LinkPath })
                {
                    if (path is null) continue;
                    var root = Normalise(path);
                    if (!IsUnder(dir, root) || root.Length <= bestLength) continue;
                    best = new WorkspaceLocation(record, repo, ModuleAt(repo, Path.GetRelativePath(root, dir)));
                    bestLength = root.Length;
                }
        return best;
    }

    InstanceRecord Update(string workspace, Func<InstanceRecord, InstanceRecord> change)
    {
        var record = instances.TryLoad(workspace) ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        var updated = change(record);
        instances.Save(updated);
        return updated;
    }

    static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // The module whose directory holds `relative` (a path under the worktree root), deepest first. A module
    // at the repo root ("" path) matches everything, so a single-module repo always names its module. Read
    // from the worktree's own config, best-effort: an unreadable config just means no module.
    static string? ModuleAt(InstanceRepo repo, string relative)
    {
        IReadOnlyList<ModuleDeclaration> modules;
        try { modules = SprigConfigLoader.LoadFromFile(Path.Combine(repo.WorktreePath, ConfigFileName)).EffectiveModules; }
        catch (SprigConfigException) { return null; }

        var rel = relative == "." ? "" : relative.Replace('\\', '/');
        return modules
            .Where(m => m.Path.Length == 0 || rel == m.Path.TrimEnd('/') || rel.StartsWith(m.Path.TrimEnd('/') + "/", PathComparison))
            .OrderByDescending(m => m.Path.Length)
            .FirstOrDefault()?.Name;
    }

    static bool IsUnder(string dir, string root)
        => string.Equals(dir, root, PathComparison)
           || dir.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    static string Normalise(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
