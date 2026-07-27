namespace Sprig.Tests;

/// <summary>
/// Groups the tests that spawn real <c>git</c> worktree operations (building and breaking sample setups) so
/// xUnit runs them serially rather than in parallel with each other. Under heavy concurrent git+disk load a
/// worktree op can occasionally hiccup, which showed up as an intermittent failure; serialising these
/// classes removes the contention without slowing the rest of the suite.
/// </summary>
[CollectionDefinition("git-heavy", DisableParallelization = true)]
public sealed class GitHeavyCollection;
