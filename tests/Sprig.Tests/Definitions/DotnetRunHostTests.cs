using Sprig.Engine.Definitions;

namespace Sprig.Tests.Definitions;

/// <summary>
/// Compiles and runs real definitions with <c>dotnet run</c>, so these take seconds each. They run one at a time:
/// they share the .NET build server and set a process-wide env var.
/// </summary>
[Collection("dotnet-run")]
[Trait("Category", "Integration")]
public sealed class DotnetRunHostTests : IDisposable
{
    readonly TempStore _store = new();
    readonly DefinitionRunner _runner;
    readonly FileTrustStore _trust;

    public DotnetRunHostTests()
    {
        _trust = new FileTrustStore(_store.Paths);
        _runner = new DefinitionRunner(_trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(90), AllowLocalSdkProject = true });
    }

    public void Dispose() => _store.Dispose();

    static string SdkProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sprig.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
    }

    string Trusted(string body)
    {
        var dir = Path.Combine(_store.Root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sprig.cs");
        File.WriteAllText(file, $"#:project {SdkProject()}\nusing Sprig;\n{body}");
        _trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));
        return file;
    }

    [Fact]
    public void A_definition_compiles_runs_and_emits_its_model()
    {
        var file = Trusted("""
            var repo = Repo.Describe("shop");
            var api = repo.DotNet("api", "src/Api").Http();
            repo.Export<Url>("api-url", api.Url);
            return repo.Emit(args);
            """);

        var result = Assert.IsType<Evaluated>(_runner.Evaluate(file));

        Assert.Equal("shop", result.Model.Name);
        Assert.Equal("{api.http.url}", result.Model.Exports[0].Value);
    }

    [Fact]
    public void Secrets_in_the_users_environment_never_reach_definition_code()
    {
        Environment.SetEnvironmentVariable("SPRIG_TEST_SECRET", "hunter2");
        try
        {
            var file = Trusted("""
                var repo = Repo.Describe("leaky");
                repo.Export<Text>("seen", Environment.GetEnvironmentVariable("SPRIG_TEST_SECRET") ?? "none");
                return repo.Emit(args);
                """);

            var result = Assert.IsType<Evaluated>(_runner.Evaluate(file));

            Assert.Equal("none", result.Model.Exports[0].Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPRIG_TEST_SECRET", null);
        }
    }

    [Fact]
    public void A_compile_error_fails_with_the_compilers_message()
    {
        var file = Trusted("""
            var repo = Repo.Describe("broken");
            repo.NoSuchMethod();
            return repo.Emit(args);
            """);

        var failed = Assert.IsType<Failed>(_runner.Evaluate(file));

        Assert.Contains("NoSuchMethod", failed.Output);
    }

    [Fact]
    public void A_definition_that_hangs_is_stopped_at_the_time_limit()
    {
        var runner = new DefinitionRunner(_trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(15), AllowLocalSdkProject = true });
        var file = Trusted("""
            Thread.Sleep(TimeSpan.FromMinutes(5));
            return 0;
            """);

        var failed = Assert.IsType<Failed>(runner.Evaluate(file));

        Assert.Contains("didn't finish", failed.Reason);
        Assert.True(failed.Elapsed < TimeSpan.FromSeconds(40));
    }
}

[CollectionDefinition("dotnet-run", DisableParallelization = true)]
public sealed class DotnetRunCollection;
