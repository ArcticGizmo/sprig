using Sprig.Engine.Git;

namespace Sprig.Engine.Workspaces;

/// <summary>A slot's branch, checked across its repos for whether it's done with.</summary>
/// <param name="Fate">For the slot as a whole: <see cref="BranchFate.Merged"/> only when every repo with work on the
/// branch has it merged, and <see cref="BranchFate.Gone"/> when a remote deleted it in any of them.</param>
/// <param name="Dirty">A worktree has changes that aren't committed, so freeing it would be refused.</param>
public sealed record SlotTidiness(string Key, string Branch, BranchFate Fate, IReadOnlyList<BranchCheck> Repos, bool Dirty)
{
    /// <summary>Done with: freeing it loses nothing, since the branch and its commits stay.</summary>
    public bool CanGo => Fate == BranchFate.Merged && !Dirty;
}

/// <param name="Unfetched">Repos whose remotes couldn't be reached, so what they say may be out of date.</param>
public sealed record TidyReport(IReadOnlyList<SlotTidiness> Slots, IReadOnlyList<string> Unfetched);

public sealed partial class WorkspaceService
{
    /// <summary>How long one repo's fetch may take before tidying up goes on with what it already has.</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Which of <paramref name="records"/>' slots are done with: each repo fetched once (the slow part, and the only one
    /// that talks to a remote), then each slot's branch checked in every repo. Asked for, never run in the background,
    /// since fetching costs a round trip per repo.
    /// </summary>
    public TidyReport TidyUp(IEnumerable<WorkspaceRecord> records, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        var all = records.ToArray();
        var slots = all.Where(r => !r.InPlace && !r.IsFree && r.Branch is not null).ToArray();
        var step = new Steps(progress);
        step.Start("Find slots with a branch");
        var passed = all.Length - slots.Length;
        step.Done($"{slots.Length} to check" + (passed == 0 ? "" : $"; {passed} passed over (main checkouts, free slots, in place)"));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var unfetched = new List<string>();
        foreach (var source in slots.SelectMany(s => s.Repos).Select(r => r.Source).Distinct(comparer).Where(Directory.Exists))
        {
            cancellation.ThrowIfCancellationRequested();
            var name = Path.GetFileName(source);
            step.Start($"Fetch {name}");
            if (git.FetchPrune(source, FetchTimeout, cancellation)) step.Done("up to date, deleted remote branches pruned");
            else
            {
                unfetched.Add(name);
                step.Warn("couldn't reach its remote; using what was fetched before");
            }
        }

        var report = new List<SlotTidiness>();
        foreach (var slot in slots)
        {
            cancellation.ThrowIfCancellationRequested();
            var branch = slot.Branch!;
            step.Start($"Check {slot.SlotName} ({branch})");
            var checks = slot.Repos.Where(r => Directory.Exists(r.Source)).Select(r => git.CheckBranch(r.Source, branch)).ToArray();
            var dirty = slot.Repos.Any(r => Directory.Exists(r.Dir) && git.HasUncommittedChanges(r.Dir));
            var found = new SlotTidiness(slot.Key, branch, Combine(checks), checks, dirty);
            if (found.Fate == BranchFate.Gone || found is { Fate: BranchFate.Merged, Dirty: true }) step.Warn(Says(found));
            else step.Done(Says(found));
            report.Add(found);
        }
        return new TidyReport(report, unfetched);
    }

    /// <summary>What was found of one slot, in words, ending on whether it can go.</summary>
    static string Says(SlotTidiness found)
    {
        var into = found.Repos.Select(c => c.Into).OfType<string>().Distinct().ToArray();
        var target = into.Length == 1 ? into[0] : "the default branch";
        return found switch
        {
            { Fate: BranchFate.Merged, Dirty: true } => $"merged into {target}, but a worktree has changes that aren't committed; keep it",
            { Fate: BranchFate.Merged } => $"merged into {target}; can be freed",
            { Fate: BranchFate.Gone } => $"its remote branch was deleted, but {target} doesn't have all of it; look before freeing",
            { Fate: BranchFate.Empty } => "nothing committed on it yet; still in use",
            _ => $"not in {target} yet; still in use",
        };
    }

    /// <summary>A slot's repos each have the branch, but often only one has work on it; the rest, never committed to,
    /// say nothing either way.</summary>
    static BranchFate Combine(IReadOnlyList<BranchCheck> checks)
    {
        var said = checks.Where(c => c.Fate != BranchFate.Empty).Select(c => c.Fate).ToArray();
        if (said.Length == 0) return checks.Count == 0 ? BranchFate.Live : BranchFate.Empty;
        if (said.Contains(BranchFate.Live)) return BranchFate.Live;
        if (said.Contains(BranchFate.Gone)) return BranchFate.Gone;
        return BranchFate.Merged;
    }
}
