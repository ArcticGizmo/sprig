using System;
using System.Collections.Generic;
using System.Linq;
using Sprig.Engine.Authoring;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Orchestrations;
using Sprig.Model;

namespace Sprig.App.ViewModels;

/// <summary>One thing a repo needs, and what fills it.</summary>
public sealed record NeedRow(string Import, string FilledBy, bool Filled);

/// <summary>A repo of the selected orchestration, as its card on the Overview shows it.</summary>
/// <param name="Path">The repo as the orchestration names it.</param>
/// <param name="Use">Its Repos-list entry, when the orchestration names it with <c>UseRepo</c>.</param>
/// <param name="InRepo">Where its definition is kept; null when there's none to use.</param>
/// <param name="Problem">Why it has no model: not approved, doesn't run, or no definition.</param>
public sealed record RepoCard(
    string Name, string Path, string? Use, bool? InRepo, string Folder,
    string Runs, string Infra, string Offers, IReadOnlyList<NeedRow> Needs, string? Problem)
{
    public string Kept => InRepo switch { true => "in repo", false => "in sprig", null => "no definition" };
    public bool InSprig => InRepo == false;
    public bool HasModel => Problem is null;
    public bool HasProblem => Problem is not null;
    public bool HasNeeds => Needs.Count > 0;
    public bool OnRepos => Use is not null;
    public bool CanAddToRepos => Use is null && System.IO.Directory.Exists(Folder);
}

/// <summary>Something the orchestration adds itself: infra it runs, or a value it supplies.</summary>
public sealed record AddedRow(string Name, string What);

/// <summary>A line of the orchestration's Groups.</summary>
public sealed record PolicyRow(string Name, string What);

/// <summary>A workspace of the selected orchestration.</summary>
public sealed record WorkspaceRow(string Name, string State, bool IsUp, bool IsFailed);

/// <summary>Builds what the orchestration page's Overview shows from a load and, when it planned, its plan.</summary>
public static class OrchestrationSummary
{
    public static string Description(LoadedOrchestration loaded)
    {
        var names = (loaded.Orchestration?.Repos ?? []).Select(r => NameOf(loaded, r)).ToArray();
        return names switch
        {
            [] => "Names no repos yet.",
            [var one] => $"Runs {one} on its own.",
            _ => $"Runs {string.Join(", ", names[..^1])} and {names[^1]} together.",
        };
    }

    static string NameOf(LoadedOrchestration loaded, RepoRefModel reference)
        => loaded.Repos.TryGetValue(reference.Path, out var repo) ? repo.Name
            : reference.Use ?? System.IO.Path.GetFileName(reference.Path.TrimEnd('/', '\\'));

    public static IReadOnlyList<RepoCard> Cards(LoadedOrchestration loaded, Plan? plan, StoredDefinitions stored)
    {
        var wires = plan?.Wires() ?? [];
        var orchestrationName = plan?.Orchestration ?? loaded.Orchestration?.Name;
        var cards = new List<RepoCard>();
        foreach (var reference in loaded.Orchestration?.Repos ?? [])
        {
            var entry = reference.Use is { } use ? stored.Repo(use) : null;
            var folder = entry?.Folder ?? loaded.RepoDirs.GetValueOrDefault(reference.Path) ?? reference.Path;
            bool? inRepo = reference.Use is null
                ? (System.IO.File.Exists(System.IO.Path.Combine(folder, OrchestrationLoader.RepoDefinitionFile)) ? true : null)
                : entry?.InRepo;
            var name = NameOf(loaded, reference);

            if (!loaded.Repos.TryGetValue(reference.Path, out var repo))
            {
                cards.Add(new RepoCard(name, reference.Path, reference.Use, inRepo, folder, "", "", "", [], WhyNoModel(loaded, reference, entry)));
                continue;
            }

            var apps = repo.Apps.Where(a => !a.Job).Select(a => a.Name).ToList();
            var jobs = repo.Apps.Where(a => a.Job).Select(a => a.Name).ToList();
            var runs = string.Join(", ", apps) + (jobs.Count > 0 ? (apps.Count > 0 ? ", " : "") + $"then {string.Join(", ", jobs)} first" : "");
            var infra = repo.Containers.Select(c => c.Name).Concat(repo.ComposeFiles.SelectMany(f => f.Services.Select(s => s.Name))).ToArray();
            var needs = repo.Imports.Select(i => Need(i.Name, reference.Path, repo.Name, loaded, wires, orchestrationName)).ToArray();
            cards.Add(new RepoCard(name, reference.Path, reference.Use, inRepo, folder,
                runs.Length > 0 ? runs : "nothing", infra.Length > 0 ? string.Join(", ", infra) : "none",
                repo.Exports.Count > 0 ? string.Join(", ", repo.Exports.Select(e => e.Name)) : "nothing", needs, null));
        }
        return cards;
    }

    static string WhyNoModel(LoadedOrchestration loaded, RepoRefModel reference, RepoEntry? entry)
    {
        if (reference.Use is { } use && entry is null) return $"'{use}' isn't on the Repos list";
        var file = entry?.DefinitionFile ?? System.IO.Path.Combine(loaded.RepoDirs.GetValueOrDefault(reference.Path) ?? reference.Path, OrchestrationLoader.RepoDefinitionFile);
        if (loaded.Untrusted.Any(u => SameFile(u.Path, file) || SameFile(u.TrustPath, file))) return "Not approved yet, so it hasn't run";
        if (!System.IO.File.Exists(file)) return "No sprig.cs yet: commit one, or add it to Repos to keep one in sprig";
        return "Its definition doesn't run; see the problems above";
    }

    static bool SameFile(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static NeedRow Need(string import, string repoPath, string owner, LoadedOrchestration loaded, IReadOnlyList<PlannedWire> wires, string? orchestrationName)
    {
        // Several repos offering it is as unfilled as none: the plan can't pick, so the orchestration has to.
        var offers = Offers(loaded, repoPath, import);
        if (offers.Count > 1 && loaded.Orchestration?.Bindings.Any(b => b.Import == import) != true)
            return new NeedRow(import, $"{string.Join(" and ", offers.Select(o => loaded.Repos[o].Name))} all offer it", false);

        var wire = wires.FirstOrDefault(w => w.ConsumerOwner == owner && w.Import == import);
        return wire switch
        {
            null => new NeedRow(import, "none of its apps use it", true),
            { Filled: false } => new NeedRow(import, "nothing fills it", false),
            { ProviderOwner: var p, Provider: { } r } when p == orchestrationName => new NeedRow(import, $"{r}, added by the orchestration", true),
            { ProviderOwner: var p } when p == orchestrationName => new NeedRow(import, "a value the orchestration sets", true),
            { ProviderOwner: var p, Provider: { } r } when p == owner => new NeedRow(import, $"its own {r}", true),
            { ProviderOwner: var p } => new NeedRow(import, p!, true),
        };
    }

    /// <summary>The orchestration's other repos that export <paramref name="import"/>, by the path the orchestration names
    /// them with.</summary>
    static IReadOnlyList<string> Offers(LoadedOrchestration loaded, string repoPath, string import)
        => loaded.Repos.Where(r => r.Key != repoPath && r.Value.Exports.Any(e => e.Name == import)).Select(r => r.Key).ToArray();

    /// <summary>Each need nothing fills, or several repos offer, with what's needed to suggest how to fill it.</summary>
    public static IReadOnlyList<(string Repo, UnfilledNeed Need)> Unfilled(LoadedOrchestration loaded, IReadOnlyList<RepoCard> cards)
        => cards.SelectMany(card => card.Needs.Where(n => !n.Filled).Select(n =>
            {
                var import = loaded.Repos.GetValueOrDefault(card.Path)?.Imports.FirstOrDefault(i => i.Name == n.Import);
                return (card.Name, new UnfilledNeed(n.Import, import?.Type ?? "text", Offers(loaded, card.Path, n.Import)));
            }))
            .GroupBy(x => x.Item2.Import)
            .Select(g => g.First())
            .ToArray();

    public static IReadOnlyList<AddedRow> Added(Plan plan)
    {
        var wires = plan.Wires().Where(w => w.ProviderOwner == plan.Orchestration).ToArray();
        var rows = plan.Containers.Where(c => c.Owner == plan.Orchestration).Select(c =>
        {
            var fills = wires.Where(w => w.Provider == c.Name).Select(w => w.Import).OfType<string>().Distinct().ToArray();
            var image = c.Image.Split(':')[0].Split('/')[^1];
            return new AddedRow(c.Name, fills.Length > 0 ? $"{image}, one per workspace. Fills {string.Join(", ", fills)}." : $"{image}, one per workspace.");
        }).ToList();
        foreach (var import in wires.Where(w => w.Provider is null).Select(w => w.Import).OfType<string>().Distinct())
            rows.Add(new AddedRow(import, "a value the orchestration sets"));
        return rows;
    }

    /// <summary>The orchestration's groups, each with what it starts.</summary>
    public static IReadOnlyList<PolicyRow> Policies(Plan plan)
        => plan.Groups.Count == 0
            ? [new("None", "Starting a workspace starts everything")]
            : plan.Groups.Select(g => new PolicyRow(g.Name, GroupMembers(plan, g))).ToArray();

    /// <summary>"shop-web, catalog-api": a repo whose every app is in the group is named once, otherwise its apps are.</summary>
    static string GroupMembers(Plan plan, PlannedGroup group)
        => string.Join(", ", group.Apps
            .Select(key => key.Split('/', 2))
            .GroupBy(parts => parts[0])
            .SelectMany(byRepo =>
            {
                var all = plan.Apps.Where(a => a.Owner == byRepo.Key).Select(a => a.Name).ToHashSet();
                var named = byRepo.Select(parts => parts[1]).ToArray();
                return all.SetEquals(named) ? [byRepo.Key] : named;
            }));
}
