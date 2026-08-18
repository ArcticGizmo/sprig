using System.Collections.Generic;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using YamlDotNet.RepresentationModel;

namespace Sprig.Tests.Workspaces;

public class WorkspaceInfraTests
{
    const string ConfigJson = """
        { "schema": 1, "name": "dotnet-api",
          "modules": [ { "name": "app", "path": "",
            "provides": [
              { "capability": "api", "outputs": { "port": { "port": true } } },
              { "capability": "postgres", "outputs": { "port": { "port": true } } } ],
            "env": [ { "file": ".env", "set": {
                "PORT": "${sprig.api.port}",
                "ConnectionStrings__Default": "Host=localhost;Port=${sprig.postgres.port};Database=librarydb" } } ],
            "compose": [ { "file": "docker-compose.yml", "overrides": [
                { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
                { "path": ["services","postgres","ports","0"], "template": "${sprig.postgres.port}:5432" } ] } ] } ] }
        """;

    const string ComposeYml = """
        services:
          postgres:
            image: postgres:17
            container_name: librarydb_postgres
            ports:
              - "6050:5432"
        """;

    static (WorkspaceService svc, InstanceStore instances, FakeDockerService docker) Build(TempStore s)
    {
        var docker = new FakeDockerService { Available = true };
        var svc = new WorkspaceService(
            new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), docker, s.Paths);
        return (svc, new InstanceStore(s.Paths), docker);
    }

    static void SeedRepo(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), ConfigJson);
        File.WriteAllText(Path.Combine(repo.Path, "docker-compose.yml"), ComposeYml);
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "add compose");
    }

    static string PortsZero(string composeFile)
    {
        var stream = new YamlStream();
        using var r = new StringReader(File.ReadAllText(composeFile));
        stream.Load(r);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var seq = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)root["services"])["postgres"])["ports"];
        return ((YamlScalarNode)seq.Children[0]).Value!;
    }

    [Fact]
    public void Create_generates_central_compose_with_allocated_postgres_port()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _, _) = Build(store);

        var record = svc.Create(repo.Path, "feat-a");

        var composePath = Assert.Single(record.Repos[0].ComposePaths);
        Assert.StartsWith(store.Root, composePath);
        var postgresPort = record.Ports["dotnet-api.postgres.port"];
        Assert.Equal($"{postgresPort}:5432", PortsZero(composePath));
        Assert.Equal(postgresPort.ToString(), record.Repos[0].Modules[0].Values["postgres.port"]);
    }

    [Fact]
    public void Up_down_reset_call_docker_with_project_name()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, docker) = Build(store);
        svc.Create(repo.Path, "feat-a");

        svc.Up("feat-a");
        Assert.Contains("sprig-feat-a", docker.Ups);
        Assert.Equal("running", instances.TryLoad("feat-a")!.LastStatus);

        svc.Down("feat-a");
        Assert.Contains(("sprig-feat-a", false), docker.Downs);

        docker.Ups.Clear();
        svc.Reset("feat-a");
        Assert.Contains("sprig-feat-a", docker.Ups);
    }

    [Fact]
    public void TryStopContainers_stops_without_tearing_down()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, docker) = Build(store);
        svc.Create(repo.Path, "feat-a");

        var stopped = svc.TryStopContainers("feat-a");

        Assert.True(stopped);
        Assert.Contains("sprig-feat-a", docker.Stops);   // compose stop, not down
        Assert.Empty(docker.Downs);                       // release is not a teardown
        Assert.Equal("stopped", instances.TryLoad("feat-a")!.LastStatus);
    }

    [Fact]
    public void TryStopContainers_is_a_no_op_when_docker_is_unavailable()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _, docker) = Build(store);
        svc.Create(repo.Path, "feat-a");
        docker.Available = false;

        Assert.False(svc.TryStopContainers("feat-a"));
        Assert.Empty(docker.Stops);
    }

    [Fact]
    public void Teardown_brings_infra_down_with_volumes_first()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _, docker) = Build(store);
        svc.Create(repo.Path, "feat-a");

        svc.Remove("feat-a");

        Assert.Contains(("sprig-feat-a", true), docker.Downs);
    }

    [Fact]
    public void Clean_teardown_deletes_the_record()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, _) = Build(store);
        svc.Create(repo.Path, "feat-a");

        svc.Remove("feat-a");

        Assert.Null(instances.TryLoad("feat-a"));
    }

    [Fact]
    public void Partial_teardown_keeps_a_flagged_record_but_still_sweeps_other_layers()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, docker) = Build(store);
        var record = svc.Create(repo.Path, "feat-a");
        var worktree = record.Repos[0].WorktreePath;

        // Infra down blows up, but the rest of the sweep must still run to completion.
        docker.DownFailure = new InvalidOperationException("compose down exploded");
        svc.Remove("feat-a");

        var kept = instances.TryLoad("feat-a");
        Assert.NotNull(kept);
        Assert.True(kept!.TeardownFailed);
        Assert.Contains(kept.TeardownIssues, i => i.Contains("stop containers"));
        // Later layers weren't skipped: the worktree folder is gone despite the infra failure.
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public void Docker_unavailable_keeps_a_flagged_record_without_calling_down()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, docker) = Build(store);
        svc.Create(repo.Path, "feat-a");
        docker.Available = false;

        svc.Remove("feat-a");

        var kept = instances.TryLoad("feat-a");
        Assert.NotNull(kept);
        Assert.True(kept!.TeardownFailed);
        Assert.Contains(kept.TeardownIssues, i => i.Contains("Docker unavailable"));
        Assert.Empty(docker.Downs);
    }

    [Fact]
    public void Retrying_teardown_after_the_blocker_clears_finishes_and_deletes_the_record()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, docker) = Build(store);
        svc.Create(repo.Path, "feat-a");

        docker.DownFailure = new InvalidOperationException("compose down exploded");
        svc.Remove("feat-a");
        Assert.True(instances.TryLoad("feat-a")!.TeardownFailed);

        // Fix the blocker and run rm again — teardown is idempotent, so the second sweep completes
        // (already-gone worktree/ports are no-ops) and the record is finally deleted.
        docker.DownFailure = null;
        svc.Remove("feat-a");

        Assert.Null(instances.TryLoad("feat-a"));
    }
}
