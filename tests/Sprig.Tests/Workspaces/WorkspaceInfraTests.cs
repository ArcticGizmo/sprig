using Sprig.Core.Compose;
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
          "ports": [ { "name": "api" }, { "name": "postgres" } ],
          "env": [ { "file": ".env", "set": {
              "PORT": "${sprig.ports.api}",
              "ConnectionStrings__Default": "Host=localhost;Port=${sprig.ports.postgres};Database=librarydb" } } ],
          "compose": { "file": "docker-compose.yml", "overrides": [
              { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
              { "path": ["services","postgres","ports","0"], "template": "${sprig.ports.postgres}:5432" } ] } }
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

        var composePath = record.Repos[0].GeneratedComposePath;
        Assert.NotNull(composePath);
        Assert.True(File.Exists(composePath));
        Assert.StartsWith(store.Root, composePath!); // in the central store, not the repo
        Assert.Equal($"{record.Ports["postgres"]}:5432", PortsZero(composePath!));
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
        Assert.Equal("stopped", instances.TryLoad("feat-a")!.LastStatus);

        docker.Ups.Clear();
        svc.Reset("feat-a");
        Assert.Contains("sprig-feat-a", docker.Ups); // reset brought it back up
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

        Assert.Contains(("sprig-feat-a", true), docker.Downs); // removeVolumes on teardown
    }

    [Fact]
    public void Infra_commands_require_docker_and_infra()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);

        // docker unavailable
        var docker = new FakeDockerService { Available = false };
        var svc = new WorkspaceService(new GitService(new ProcessRunner()), new FilePortStore(store.Paths),
            new InstanceStore(store.Paths), new EnvClobberService(), new ComposeGenerator(), docker, store.Paths);
        svc.Create(repo.Path, "feat-a");
        Assert.Throws<WorkspaceException>(() => svc.Up("feat-a"));

        // unknown workspace
        docker.Available = true;
        Assert.Throws<WorkspaceException>(() => svc.Up("ghost"));
    }
}
