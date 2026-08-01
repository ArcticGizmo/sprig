using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>A schema-3 repo with several modules materialises each module under its own path: env files
/// land in the module's directory, setup runs there, and each module's compose generates a distinct file.</summary>
public class WorkspaceModuleTests
{
    // Two modules, each in its own subdirectory. Only ${sprig.workspace} is referenced, so the repo needs
    // no stack inputs and can stand up via the ad-hoc single-repo path.
    const string TwoModuleConfig = """
        { "schema": 3, "name": "mono",
          "modules": [
            { "name": "web", "path": "apps/web",
              "env": [ { "file": ".env.local", "set": { "NAME": "web--${sprig.workspace}" } } ],
              "setup": [ "npm ci" ] },
            { "name": "api", "path": "apps/api",
              "env": [ { "file": ".env", "set": { "NAME": "api--${sprig.workspace}" } } ],
              "compose": [ { "file": "docker-compose.yml", "overrides": [
                  { "path": ["services","db","container_name"], "template": "db--${sprig.workspace}" } ] } ],
              "setup": [ "dotnet restore" ] } ] }
        """;

    const string ApiComposeYml = """
        services:
          db:
            image: postgres:17
            container_name: db
        """;

    static (WorkspaceService svc, InstanceStore store, RecordingProcessRunner setupRunner) Build(TempStore s)
    {
        var git = new GitService(new ProcessRunner());
        var setupRunner = new RecordingProcessRunner { ExitCode = 0 };
        var svc = new WorkspaceService(git, new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths,
            new SetupRunner(setupRunner));
        return (svc, new InstanceStore(s.Paths), setupRunner);
    }

    static void SeedRepo(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), TwoModuleConfig);
        Directory.CreateDirectory(Path.Combine(repo.Path, "apps", "api"));
        File.WriteAllText(Path.Combine(repo.Path, "apps", "api", "docker-compose.yml"), ApiComposeYml);
    }

    [Fact]
    public void Each_module_env_lands_under_its_own_path()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _, _) = Build(store);

        svc.Create(repo.Path, "feat-a");
        var worktree = repo.SiblingWorktree("feat-a");

        Assert.True(File.Exists(Path.Combine(worktree, "apps", "web", ".env.local")));
        Assert.True(File.Exists(Path.Combine(worktree, "apps", "api", ".env")));
        Assert.Contains("NAME=web--feat-a", File.ReadAllText(Path.Combine(worktree, "apps", "web", ".env.local")));
    }

    [Fact]
    public void Each_module_setup_runs_in_its_module_directory_and_is_labelled()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _, runner) = Build(store);

        var record = svc.Create(repo.Path, "feat-a");
        var worktree = repo.SiblingWorktree("feat-a");

        Assert.Contains(runner.Calls, c => c.Arguments[^1] == "npm ci"
            && c.WorkingDirectory == Path.Combine(worktree, "apps", "web"));
        Assert.Contains(runner.Calls, c => c.Arguments[^1] == "dotnet restore"
            && c.WorkingDirectory == Path.Combine(worktree, "apps", "api"));

        Assert.Equal(["npm ci", "dotnet restore"], record.Repos[0].Setup.Select(o => o.Command));
        Assert.Equal(["web", "api"], record.Repos[0].Setup.Select(o => o.Module));
    }

    [Fact]
    public void A_module_compose_generates_a_distinct_file_named_after_the_module()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _, _) = Build(store);

        var record = svc.Create(repo.Path, "feat-a");

        var composePath = Assert.Single(record.Repos[0].ComposePaths);
        Assert.Contains(".api.", Path.GetFileName(composePath));   // module segment prevents collisions
        Assert.EndsWith(".sprig.yml", composePath);
        Assert.Contains("db--feat-a", File.ReadAllText(composePath));
    }
}
