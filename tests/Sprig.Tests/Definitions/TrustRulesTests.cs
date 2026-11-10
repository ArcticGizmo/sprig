using Sprig.Engine.Definitions;
using Sprig.Engine.Git;
using Sprig.Engine.Processes;

namespace Sprig.Tests.Definitions;

public sealed class TrustRulesTests : IDisposable
{
    readonly TempStore _store = new();
    readonly FileTrustStore _trust;
    readonly TrustRules _rules;
    readonly ProcessRunner _processes = new();

    public TrustRulesTests()
    {
        _trust = new FileTrustStore(_store.Paths);
        _rules = new TrustRules(_trust, new StoredDefinitions(_store.Paths), new GitClient(_processes));
    }

    public void Dispose() => _store.Dispose();

    TrustState Check(string file, string? trustAs = null) => _rules.Check(file, DefinitionHash.Of(File.ReadAllText(file)), trustAs).State;

    void Approve(string file) => _trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));

    static string Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    void Git(string dir, params string[] args) => _processes.Run("git", ["-C", dir, .. args]).EnsureSuccess();

    void Commit(string dir) => Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-am", "x");

    string Repo()
    {
        var dir = Path.Combine(_store.Root, "src", "shop");
        Write(Path.Combine(dir, "sprig.cs"), "// one");
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "add", ".");
        Commit(dir);
        return dir;
    }

    [Fact]
    public void An_approved_definition_kept_in_sprig_stays_approved_however_it_is_edited()
    {
        var file = Write(Path.Combine(_store.Paths.Root, "repos", "shop", "sprig.cs"), "// one");
        Approve(file);

        Write(file, "// two, from another editor");

        Assert.Equal(TrustState.Trusted, Check(file));
        // Recorded, so the trust store agrees without the rules.
        Assert.Equal(TrustState.Trusted, _trust.Check(file, DefinitionHash.Of("// two, from another editor")).State);
    }

    [Fact]
    public void A_definition_copied_into_sprig_unread_still_asks()
    {
        var file = Write(Path.Combine(_store.Paths.Root, "repos", "shop", "sprig.cs"), "// from someone's repo");

        Assert.Equal(TrustState.Unknown, Check(file));
    }

    [Fact]
    public void An_uncommitted_edit_to_an_approved_version_in_a_repo_is_yours_and_stays_approved_once_committed()
    {
        var dir = Repo();
        var file = Path.Combine(dir, "sprig.cs");
        Approve(file);

        Write(file, "// two, not committed");
        Assert.Equal(TrustState.Trusted, Check(file));

        Commit(dir);
        Assert.Equal(TrustState.Trusted, Check(file));
    }

    [Fact]
    public void A_version_that_arrived_in_a_commit_asks()
    {
        var dir = Repo();
        var file = Path.Combine(dir, "sprig.cs");
        Approve(file);

        Write(file, "// two, from a pulled branch");
        Commit(dir);

        Assert.Equal(TrustState.Changed, Check(file));
    }

    [Fact]
    public void An_edit_to_a_committed_version_nobody_approved_asks()
    {
        var dir = Repo();
        var file = Path.Combine(dir, "sprig.cs");
        Approve(file);
        Write(file, "// two, from a pulled branch");
        Commit(dir);

        Write(file, "// three, on top of it");

        Assert.Equal(TrustState.Changed, Check(file));
    }

    [Fact]
    public void A_worktree_s_edit_counts_against_the_repo_s_approvals()
    {
        var dir = Repo();
        var file = Path.Combine(dir, "sprig.cs");
        Approve(file);
        var worktree = Path.Combine(_store.Root, "src", "shop--1");
        Git(dir, "worktree", "add", "-q", "-b", "feat", worktree);

        var copy = Write(Path.Combine(worktree, "sprig.cs"), "// two, in the worktree");

        Assert.Equal(TrustState.Trusted, Check(copy, trustAs: file));
    }
}
