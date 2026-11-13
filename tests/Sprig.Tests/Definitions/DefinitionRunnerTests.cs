using Sprig.Engine.Definitions;

namespace Sprig.Tests.Definitions;

public class TrustStoreTests
{
    [Fact]
    public void Unknown_then_trusted_then_changed_when_the_content_moves_on()
    {
        using var store = new TempStore();
        var trust = new FileTrustStore(store.Paths);
        var file = Path.Combine(store.Root, "repo", "sprig.yaml");

        Assert.Equal(TrustState.Unknown, trust.Check(file, "aaa").State);

        trust.Approve(file, "aaa");
        Assert.Equal(TrustState.Trusted, trust.Check(file, "aaa").State);

        var changed = trust.Check(file, "bbb");
        Assert.Equal(TrustState.Changed, changed.State);
        Assert.Equal("aaa", changed.ApprovedSha256);

        trust.Revoke(file);
        Assert.Equal(TrustState.Unknown, trust.Check(file, "aaa").State);
    }

    [Fact]
    public void Approving_a_new_version_keeps_the_earlier_ones()
    {
        using var store = new TempStore();
        var trust = new FileTrustStore(store.Paths);
        var file = Path.Combine(store.Root, "repo", "sprig.yaml");

        trust.Approve(file, "aaa");
        trust.Approve(file, "bbb");

        Assert.Equal(TrustState.Trusted, trust.Check(file, "aaa").State);
        Assert.Equal(TrustState.Trusted, trust.Check(file, "bbb").State);
        Assert.Equal("bbb", trust.Check(file, "ccc").ApprovedSha256);
    }

    [Fact]
    public void Only_the_most_recent_versions_are_kept()
    {
        using var store = new TempStore();
        var trust = new FileTrustStore(store.Paths);
        var file = Path.Combine(store.Root, "repo", "sprig.yaml");

        for (var i = 0; i <= FileTrustStore.VersionsKept; i++) trust.Approve(file, $"v{i}");

        Assert.Equal(FileTrustStore.VersionsKept, trust.List().Count);
        Assert.Equal(TrustState.Changed, trust.Check(file, "v0").State);
        Assert.Equal(TrustState.Trusted, trust.Check(file, $"v{FileTrustStore.VersionsKept}").State);
    }

    [Fact]
    public void Line_endings_alone_do_not_change_the_hash()
        => Assert.Equal(DefinitionHash.Of("a\r\nb\r\n"), DefinitionHash.Of("a\nb\n"));
}

public class DefinitionRunnerTests
{
    const string Yaml = "schema: 1\nrepo: r\napps:\n  web: { run: node }\n";

    static (TempStore store, string file) Definition(string text = Yaml)
    {
        var store = new TempStore();
        var dir = Path.Combine(store.Root, "repo");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sprig.yaml");
        File.WriteAllText(file, text);
        return (store, file);
    }

    [Fact]
    public void An_untrusted_definition_is_never_read()
    {
        var (store, file) = Definition();
        using var _ = store;
        var reads = 0;

        var result = new DefinitionRunner(new FileTrustStore(store.Paths), store.Paths, read: (t, p) => { reads++; return DefinitionReader.Read(t, p); }).Evaluate(file);

        Assert.IsType<NeedsTrust>(result);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void A_trusted_definition_is_read_into_its_model()
    {
        var (store, file) = Definition();
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));

        var model = Assert.IsType<Evaluated>(new DefinitionRunner(trust, store.Paths).Evaluate(file)).Model;

        Assert.Equal("web", Assert.Single(Assert.IsType<Sprig.Model.RepoModel>(model).Apps).Name);
    }

    [Fact]
    public void A_mistake_comes_back_with_its_line()
    {
        var (store, file) = Definition(Yaml + "  api: { run: nodejs }\n");
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));

        var problem = Assert.Single(Assert.IsType<Invalid>(new DefinitionRunner(trust, store.Paths).Evaluate(file)).Problems);

        Assert.Equal((5, file), (problem.Location!.Line, problem.Location.File));
        Assert.Contains("dotnet, node, command", problem.Message);
    }

    [Fact]
    public void Editing_a_trusted_definition_asks_again_and_names_the_approved_version()
    {
        var (store, file) = Definition();
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        var approved = DefinitionHash.Of(File.ReadAllText(file));
        trust.Approve(file, approved);
        File.AppendAllText(file, "# changed on another branch\n");

        var result = Assert.IsType<NeedsTrust>(new DefinitionRunner(trust, store.Paths).Evaluate(file));

        Assert.Equal(approved, result.ApprovedSha256);
    }

    [Fact]
    public void A_copy_is_trusted_by_the_approvals_of_the_file_it_stands_for()
    {
        var (store, original) = Definition();
        using var _ = store;
        var copy = Path.Combine(store.Root, "worktree", "sprig.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(original, copy);
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(original, DefinitionHash.Of(File.ReadAllText(original)));
        var runner = new DefinitionRunner(trust, store.Paths);

        Assert.IsType<NeedsTrust>(runner.Evaluate(copy));
        Assert.IsType<Evaluated>(runner.Evaluate(copy, trustAs: original));

        File.AppendAllText(copy, "# the branch's change\n");
        var pending = Assert.IsType<NeedsTrust>(runner.Evaluate(copy, trustAs: original));
        Assert.Equal(original, pending.TrustPath);
        Assert.Equal(copy, pending.Path);
        Assert.True(pending.IsCopy);
    }
}
