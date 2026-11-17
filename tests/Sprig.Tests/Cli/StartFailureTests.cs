using Sprig.Cli.Commands;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Cli;

/// <summary>What <c>start --json</c> and <c>up --json</c> say when apps don't become ready.</summary>
public sealed class StartFailureTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sprig-start-failure-" + Guid.NewGuid().ToString("N"));

    public StartFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    static Plan Shop()
    {
        var orchestration = Yaml.Orchestration("schema: 1\norchestration: shop\nrepos:\n  shop-services: { path: shop-services }\n");
        var services = Yaml.Repo("""
            schema: 1
            repo: shop-services
            apps:
              api:
                run: dotnet
                path: src/Api
                ports: { http: any }
                health: /health
              worker:
                run: command
                command: node worker.js
                ready-log: listening
              migrate:
                run: command
                command: node migrate.js
                once: true
            """);
        return Planner.Build(new PlanInput(orchestration, new Dictionary<string, RepoModel> { ["shop-services"] = services },
            "1", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
    }

    RunnerStatus Runner(string name, RunnerState state, int? exit = null, string? error = null, params string[] log)
    {
        var file = Path.Combine(_dir, name.Replace('/', '-') + ".log");
        File.WriteAllLines(file, log);
        return new RunnerStatus("shop/1", name, state, null, null, exit, file, DateTimeOffset.Now, error);
    }

    [Fact]
    public void All_ready_is_no_failure()
    {
        Assert.Null(StartFailure.Of(Shop(), [Runner("shop-services/api", RunnerState.Ready), Runner("shop-services/migrate", RunnerState.Completed)]));
    }

    [Fact]
    public void Each_app_that_failed_says_why_what_ready_meant_and_how_its_log_ends()
    {
        var log = Enumerable.Range(1, 40).Select(i => $"line {i}").Append("Unhandled exception: no connection string").ToArray();

        var failure = StartFailure.Of(Shop(), [
            Runner("shop-services/api", RunnerState.Exited, exit: 1, log: log),
            Runner("shop-services/migrate", RunnerState.Failed, error: "failed 3 times"),
            Runner("shop-services/worker", RunnerState.Ready),
        ])!;

        Assert.Equal(StartFailure.Failed, failure.Code);
        Assert.Equal("shop-services/api: exited with code 1; shop-services/migrate: failed 3 times", failure.Error);
        var api = failure.Apps[0];
        Assert.Equal("http answers /health with success", api.ReadyWhen);
        Assert.Equal(30, api.LastLines.Count);
        Assert.Equal("Unhandled exception: no connection string", api.LastLines[^1]);
        Assert.Equal("it finishes with exit code 0", failure.Apps[1].ReadyWhen);
    }

    [Fact]
    public void An_app_still_starting_when_sprig_stops_waiting_is_a_time_out()
    {
        var failure = StartFailure.Of(Shop(), [Runner("shop-services/worker", RunnerState.Starting)])!;

        Assert.Equal(StartFailure.TimedOut, failure.Code);
        Assert.Equal("still starting when sprig stopped waiting", failure.Apps[0].Why);
        Assert.Equal("a line of its output matches listening", failure.Apps[0].ReadyWhen);
    }
}
