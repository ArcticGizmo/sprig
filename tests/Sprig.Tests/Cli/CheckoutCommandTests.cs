using Sprig.Cli;
using Sprig.Engine.Processes;
using Sprig.Engine.Store;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

/// <summary>Checkout makes a branch ready in a slot and starts nothing; up only runs what's checked out. Real git repos in
/// a temp folder, with no infra, so docker and the host are never needed.</summary>
[Collection("cli-console")]
public sealed class CheckoutCommandTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root = LongPath.Of(Path.Combine(Path.GetTempPath(), "sprig-checkout-" + Guid.NewGuid().ToString("N")));
    readonly ProcessRunner _processes = new();

    public CheckoutCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Only up starts the host; with none running this returns at once.
        new Sprig.Engine.Hosting.HostClient(_store.Paths).StopAllAndShutdown();
        _store.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // git marks its objects read-only
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    (int code, string stdout, string stderr) Run(params string[] args)
    {
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = CliApp.Run(args, _store.Paths);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    const string ShopServices = """
        schema: 1
        repo: shop-services
        apps:
          api:
            run: command
            command: node server.js
            ports: { http: any }
        files:
          .env:
            read-by: [api]
            values:
              PORT: ${api.port}
        """;

    void ShopServicesRepo()
    {
        var dir = Path.Combine(_root, "shop-services");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), ".env\n");
        File.WriteAllText(Path.Combine(dir, "sprig.yaml"), ShopServices);
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        Assert.Equal(0, Run("repo", "add", dir).code);
        Assert.Equal(0, Run("trust", "add", Path.Combine(dir, "sprig.yaml")).code);
    }

    void Git(string dir, params string[] args) => _processes.Run("git", ["-C", dir, .. args]).EnsureSuccess();

    WorkspaceRecord[] Slots() => new WorkspaceStore(_store.Paths).List().Where(r => !r.IsMain).ToArray();

    [Fact]
    public void Checkout_makes_the_branch_ready_in_a_slot_and_starts_nothing()
    {
        ShopServicesRepo();

        var (code, stdout, stderr) = Run("checkout", "shop-services", "-b", "feat-x", "--json");

        Assert.True(code == 0, stderr);
        var slot = Assert.Single(Slots());
        Assert.Equal("feat-x", slot.Branch);
        Assert.Contains("PORT=", File.ReadAllText(Path.Combine(slot.Folder!, ".env")));
        Assert.DoesNotContain("\"runners\"", stdout);
    }

    [Fact]
    public void Checkout_needs_a_branch()
    {
        var (code, _, stderr) = Run("checkout", "shop-services");

        Assert.Equal(1, code);
        Assert.Contains("sprig checkout -b <branch>", stderr);
    }

    [Fact]
    public void Up_with_a_branch_or_no_start_says_to_check_out_first_and_makes_nothing()
    {
        ShopServicesRepo();

        var (code, _, stderr) = Run("up", "shop-services", "-b", "feat-x");
        Assert.Equal(1, code);
        Assert.Contains("sprig checkout -b feat-x", stderr);
        Assert.Contains("sprig up feat-x", stderr);

        (code, var stdout, _) = Run("up", "shop-services", "--no-start", "--json");
        Assert.Equal(1, code);
        Assert.Contains("\"ok\": false", stdout);
        Assert.Contains("use: sprig checkout -b", stdout);

        Assert.Empty(Slots());
    }

    [Fact]
    public void Up_refuses_a_workspace_that_isnt_checked_out_rather_than_making_one()
    {
        ShopServicesRepo();

        var (code, _, stderr) = Run("up", "shop-services", "feat-y");

        Assert.Equal(1, code);
        Assert.Contains("sprig checkout shop-services -b feat-y", stderr);
        Assert.Empty(Slots());
    }

    [Fact]
    public void Up_with_no_wait_returns_once_the_apps_are_asked_for_naming_them()
    {
        ShopServicesRepo();
        Assert.Equal(0, Run("checkout", "shop-services", "-b", "feat-x").code);

        var (code, _, stderr) = Run("up", "shop-services", "feat-x", "--run", "--no-wait");
        Assert.Equal(1, code);
        Assert.Contains("one or the other", stderr);

        (code, var stdout, stderr) = Run("up", "shop-services", "feat-x", "--no-wait", "--json");

        Assert.True(code == 0, stderr + stdout);
        using var json = System.Text.Json.JsonDocument.Parse(stdout);
        Assert.Equal(["shop-services/api"], json.RootElement.GetProperty("apps").EnumerateArray().Select(a => a.GetString()));
        Assert.False(json.RootElement.TryGetProperty("runners", out _));
    }

    [Fact]
    public void Up_refuses_a_free_slot_naming_the_checkout_that_fills_it()
    {
        ShopServicesRepo();
        Assert.Equal(0, Run("checkout", "shop-services", "-b", "feat-x").code);
        Assert.Equal(0, Run("free", "shop-services", "feat-x").code);

        var (code, _, stderr) = Run("up", "shop-services", "1");

        Assert.Equal(1, code);
        Assert.Contains("is free", stderr);
        Assert.Contains("sprig checkout shop-services -b <branch> --slot 1", stderr);
    }
}
