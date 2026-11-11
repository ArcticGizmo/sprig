using Sprig.Engine.Definitions;

namespace Sprig.Tests.Definitions;

public class DirectivePolicyTests
{
    [Theory]
    [InlineData("#:package ArcticGizmo.Sprig.Sdk@0.9.0")]
    [InlineData("#:package arcticgizmo.sprig.sdk@0.*")]
    public void The_sdk_package_is_allowed(string directive)
        => Assert.Empty(DirectivePolicy.Check(directive + "\nusing Sprig;", allowLocalSdkProject: false));

    [Theory]
    [InlineData("#:package Newtonsoft.Json@13.0.3", "only the ArcticGizmo.Sprig.Sdk package")]
    [InlineData("#:package Sprig.Sdk@0.1.0", "only the ArcticGizmo.Sprig.Sdk package")]
    [InlineData("#:project ../Other/Other.csproj", "local project")]
    [InlineData("#:property TreatWarningsAsErrors=false", "#:property")]
    [InlineData("#:sdk Microsoft.NET.Sdk.Web", "#:sdk")]
    public void Anything_else_is_rejected_with_its_line(string directive, string reason)
    {
        var violation = Assert.Single(DirectivePolicy.Check("using Sprig;\n" + directive, allowLocalSdkProject: true));

        Assert.Equal(2, violation.Line);
        Assert.Contains(reason, violation.Reason);
    }

    [Fact]
    public void A_local_sdk_project_is_allowed_only_in_dev()
    {
        const string text = "#:project ../../src/Sprig.Sdk/Sprig.Sdk.csproj";

        Assert.Empty(DirectivePolicy.Check(text, allowLocalSdkProject: true));
        Assert.Single(DirectivePolicy.Check(text, allowLocalSdkProject: false));
    }
}

public class TrustStoreTests
{
    [Fact]
    public void Unknown_then_trusted_then_changed_when_the_content_moves_on()
    {
        using var store = new TempStore();
        var trust = new FileTrustStore(store.Paths);
        var file = Path.Combine(store.Root, "repo", "sprig.cs");

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
        var file = Path.Combine(store.Root, "repo", "sprig.cs");

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
        var file = Path.Combine(store.Root, "repo", "sprig.cs");

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
    sealed class FakeHost(Func<string, HostResult> run) : IDefinitionHost
    {
        public int Runs { get; private set; }

        public HostResult Run(string definitionPath, string outputFile, TimeSpan timeout)
        {
            Runs++;
            return run(outputFile);
        }
    }

    static readonly string ModelJson = $$"""
        { "schema": {{Sprig.Sdk.Schema}}, "kind": "repo", "name": "r", "sdkVersion": "0", "location": { "file": "f", "line": 1 },
          "apps": [], "containers": [], "imports": [], "exports": [] }
        """;

    static (TempStore store, string file) Definition(string text = "using Sprig;")
    {
        var store = new TempStore();
        var dir = Path.Combine(store.Root, "repo");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sprig.cs");
        File.WriteAllText(file, text);
        return (store, file);
    }

    [Fact]
    public void An_untrusted_definition_is_never_run()
    {
        var (store, file) = Definition();
        using var _ = store;
        var host = new FakeHost(_ => throw new InvalidOperationException("must not run"));

        var result = new DefinitionRunner(new FileTrustStore(store.Paths), host, store.Paths).Evaluate(file);

        Assert.IsType<NeedsTrust>(result);
        Assert.Equal(0, host.Runs);
    }

    [Fact]
    public void A_rejected_directive_stops_it_before_trust_is_even_asked()
    {
        var (store, file) = Definition("#:package Evil.Package@1.0.0\nusing Sprig;");
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));
        var host = new FakeHost(_ => throw new InvalidOperationException("must not run"));

        var result = new DefinitionRunner(trust, host, store.Paths).Evaluate(file);

        Assert.IsType<Rejected>(result);
        Assert.Equal(0, host.Runs);
    }

    [Fact]
    public void A_trusted_definition_runs_once_and_then_comes_from_the_cache()
    {
        var (store, file) = Definition();
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));
        var host = new FakeHost(outFile => { File.WriteAllText(outFile, ModelJson); return new HostResult(0, "", false); });
        var runner = new DefinitionRunner(trust, host, store.Paths);

        var first = Assert.IsType<Evaluated>(runner.Evaluate(file));
        var second = Assert.IsType<Evaluated>(runner.Evaluate(file));

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, host.Runs);
    }

    [Fact]
    public void Editing_a_trusted_definition_asks_again_and_names_the_approved_version()
    {
        var (store, file) = Definition();
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        var approved = DefinitionHash.Of(File.ReadAllText(file));
        trust.Approve(file, approved);
        File.AppendAllText(file, "\n// changed on another branch");

        var result = Assert.IsType<NeedsTrust>(
            new DefinitionRunner(trust, new FakeHost(_ => throw new InvalidOperationException()), store.Paths).Evaluate(file));

        Assert.Equal(approved, result.ApprovedSha256);
    }

    [Fact]
    public void A_copy_is_trusted_by_the_approvals_of_the_file_it_stands_for()
    {
        var (store, original) = Definition();
        using var _ = store;
        var copy = Path.Combine(store.Root, "worktree", "sprig.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(original, copy);
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(original, DefinitionHash.Of(File.ReadAllText(original)));
        var host = new FakeHost(outFile => { File.WriteAllText(outFile, ModelJson); return new HostResult(0, "", false); });
        var runner = new DefinitionRunner(trust, host, store.Paths);

        Assert.IsType<NeedsTrust>(runner.Evaluate(copy));
        Assert.IsType<Evaluated>(runner.Evaluate(copy, trustAs: original));

        File.AppendAllText(copy, "\n// the branch's change");
        var pending = Assert.IsType<NeedsTrust>(runner.Evaluate(copy, trustAs: original));
        Assert.Equal(original, pending.TrustPath);
        Assert.Equal(copy, pending.Path);
        Assert.True(pending.IsCopy);
    }

    [Fact]
    public async Task Asking_for_the_same_file_twice_at_once_runs_it_once_and_the_second_ask_reads_the_cache()
    {
        var (store, file) = Definition();
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));
        var running = 0;
        var overlapped = false;
        var host = new FakeHost(outFile =>
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            Thread.Sleep(200);
            File.WriteAllText(outFile, ModelJson);
            Interlocked.Decrement(ref running);
            return new HostResult(0, "", false);
        });
        var runner = new DefinitionRunner(trust, host, store.Paths);

        var results = await Task.WhenAll(Task.Run(() => runner.Evaluate(file)), Task.Run(() => runner.Evaluate(file)));

        Assert.False(overlapped);
        Assert.Equal(1, host.Runs);
        Assert.All(results, r => Assert.IsType<Evaluated>(r));
        Assert.Single(results, r => ((Evaluated)r).FromCache);
    }

    [Fact]
    public void A_definition_that_writes_no_model_says_what_is_probably_missing()
    {
        var (store, file) = Definition();
        using var _ = store;
        var trust = new FileTrustStore(store.Paths);
        trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));

        var result = new DefinitionRunner(trust, new FakeHost(_ => new HostResult(0, "", false)), store.Paths).Evaluate(file);

        Assert.Contains("repo.Emit(args)", Assert.IsType<Failed>(result).Reason);
    }
}
