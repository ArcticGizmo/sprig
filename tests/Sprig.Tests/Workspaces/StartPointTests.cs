using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>The "start from" picker's data: how <see cref="WorkspaceService.StartPoints"/> ranks candidates
/// and flags the chips, and how <see cref="StartPointFilter"/> decides recent-vs-search. Pure logic over a
/// fake git, so no real repo needed.</summary>
public class StartPointTests
{
    static WorkspaceService Build(FakeGitService git, TempStore store) =>
        new(git, new FilePortStore(store.Paths), new InstanceStore(store.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService(), store.Paths, null);

    static DateTimeOffset At(int year, int month) => new(year, month, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartPoints_orders_current_then_default_then_recent_and_flags_chips()
    {
        using var store = new TempStore();
        var git = new FakeGitService { DefaultBase = "upstream/main", Current = "feature-x" };
        git.StartPointCandidates.Add(new BranchRef("origin/old", At(2020, 1)));
        git.StartPointCandidates.Add(new BranchRef("upstream/main", At(2026, 1)));
        git.StartPointCandidates.Add(new BranchRef("feature-x", At(2026, 8)));
        var svc = Build(git, store);

        var opts = svc.StartPoints(["/repo"]);

        Assert.Equal("upstream/main", opts.Default);
        // current (feature-x) leads, then the default main/master, then the rest by recency.
        Assert.Equal(["feature-x", "upstream/main", "origin/old"], opts.Candidates.Select(c => c.Ref));
        Assert.True(opts.Candidates.Single(c => c.Ref == "feature-x").IsCurrent);
        Assert.True(opts.Candidates.Single(c => c.Ref == "upstream/main").IsDefaultBranch);
        Assert.Contains("/repo", git.Fetched); // fetched so the list is current
    }

    [Fact]
    public void StartPointFilter_shows_recent_when_empty_and_searches_all_otherwise()
    {
        var all = new List<StartPointChoice>
        {
            new("upstream/main", null, true, false),
            new("feature-a", null, false, false),
            new("release/2.0", null, false, false),
        };

        Assert.Equal(2, StartPointFilter.Apply(all, "", 2).Count);   // no search → capped to the recent few
        Assert.Equal(3, StartPointFilter.Apply(all, "  ", 10).Count); // whitespace = empty; under cap → all
        Assert.Equal(["release/2.0"], StartPointFilter.Apply(all, "rel", 1).Select(c => c.Ref)); // search ignores the cap
        Assert.Empty(StartPointFilter.Apply(all, "zzz", 5));         // no match
    }
}
