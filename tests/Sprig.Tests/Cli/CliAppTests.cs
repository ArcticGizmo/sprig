using Sprig.Cli;

namespace Sprig.Tests.Cli;

[Collection("cli-console")]
public class CliAppTests
{
    static (int code, string stdout, string stderr) Run(params string[] args)
    {
        using var store = new TempStore();
        return Run(store, args);
    }

    static (int code, string stdout, string stderr) Run(TempStore store, params string[] args)
    {
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = CliApp.Run(args, store.Paths);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    [Fact]
    public void Help_lists_only_the_commands_this_build_has()
    {
        var (code, stdout, _) = Run("--help");

        Assert.Equal(0, code);
        Assert.Contains("open", stdout);
        Assert.Contains("update", stdout);
        Assert.Contains("shell", stdout);
        Assert.DoesNotContain("pool", stdout);
        Assert.DoesNotContain("map", stdout);
    }

    [Fact]
    public void Ps_with_nothing_running_says_how_to_start_something()
    {
        var (code, stdout, stderr) = Run("ps");

        Assert.Equal(0, code);
        Assert.Contains("sprig start [id] [workspace]", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void An_unknown_command_fails_with_exit_code_1_and_a_json_error_when_asked()
    {
        var (code, stdout, _) = Run("ws", "ls", "--json");

        Assert.Equal(1, code);
        Assert.Contains("\"ok\": false", stdout);
    }

    [Fact]
    public void Repos_are_added_copied_into_sprig_and_removed_from_the_command_line_and_adding_twice_is_harmless()
    {
        using var store = new TempStore();
        var web = Path.Combine(store.Root, "src", "shop-web");
        var mobile = Path.Combine(store.Root, "src", "shop-mobile");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(mobile);
        File.WriteAllText(Path.Combine(web, "sprig.yaml"), "// shop-web's own");
        File.WriteAllText(Path.Combine(mobile, "package.json"), """{ "name": "mobile", "scripts": { "dev": "vite" } }""");
        var stored = new Sprig.Engine.Definitions.StoredDefinitions(store.Paths);

        Assert.Equal(0, Run(store, "repo", "add", web).code);
        Assert.Equal(0, Run(store, "repo", "add", web).code);
        Assert.Equal(0, Run(store, "repo", "add", web, "--id", "shop-web").code);
        Assert.Equal(1, Run(store, "repo", "add", web, "--id", "web-again").code);
        Assert.Equal(0, Run(store, "repo", "add", mobile, "--id", "mobile").code);
        Assert.Equal(0, Run(store, "repo", "copy", "shop-web", "--id", "web-sprig").code);
        File.WriteAllText(stored.CopyFile("web-sprig"), "// edited in sprig");
        Assert.Equal(0, Run(store, "repo", "copy", "shop-web", "--id", "web-sprig").code);

        Assert.Equal(["mobile", "shop-web", "web-sprig"], stored.Repos().Select(e => e.Id));
        Assert.True(stored.Repo("shop-web")!.InRepo);
        Assert.False(stored.Repo("mobile")!.InRepo);
        Assert.True(File.Exists(stored.CopyFile("mobile")));
        Assert.Equal("// edited in sprig", File.ReadAllText(stored.CopyFile("web-sprig")));
        Assert.Contains("\"inRepo\": false", Run(store, "repo", "ls", "--json").stdout);

        // A repo an orchestration uses stays unless removal is forced.
        var orchestration = Path.Combine(store.Root, "src", "shop.orchestration.yaml");
        File.WriteAllText(orchestration, "schema: 1\norchestration: shop\nrepos:\n  web: { use: web-sprig }\n");
        new Sprig.Engine.Orchestrations.OrchestrationRegistry(store.Paths).Add("shop", orchestration);
        var (refused, _, why) = Run(store, "repo", "rm", "web-sprig");
        Assert.Equal(1, refused);
        Assert.Contains("used by shop", why);
        Assert.Equal(0, Run(store, "repo", "rm", "web-sprig", "--force").code);
        Assert.Null(stored.Repo("web-sprig"));
        Assert.True(File.Exists(Path.Combine(web, "sprig.yaml")));

        // --in-sprig keeps a copy of the folder's own file, even with that file already listed.
        Assert.Equal(0, Run(store, "repo", "add", web, "--in-sprig").code);
        Assert.Equal("// shop-web's own", File.ReadAllText(stored.Repo("shop-web-sprig")!.DefinitionFile));
    }

    [Fact]
    public void Init_drafts_a_definition_kept_in_sprig_or_written_into_the_folder_and_running_it_twice_is_harmless()
    {
        using var store = new TempStore();
        var web = Path.Combine(store.Root, "src", "shop-web");
        var api = Path.Combine(store.Root, "src", "api");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(web, "package.json"), """{ "name": "web", "scripts": { "dev": "vite" } }""");
        File.WriteAllText(Path.Combine(api, "package.json"), """{ "name": "api", "scripts": { "dev": "node server.js" } }""");
        var stored = new Sprig.Engine.Definitions.StoredDefinitions(store.Paths);

        var (code, stdout, _) = Run(store, "init", web, "--json");
        Assert.Equal(0, code);
        Assert.Contains("\"existed\": false", stdout);
        Assert.False(stored.Repo("shop-web")!.InRepo);
        Assert.False(File.Exists(Path.Combine(web, "sprig.yaml")));
        Assert.Contains("\"existed\": true", Run(store, "init", web, "--json").stdout);

        Assert.Equal(0, Run(store, "init", api, "--in-repo").code);
        Assert.True(stored.Repo("api")!.InRepo);
        Assert.Contains("run: node", File.ReadAllText(Path.Combine(api, "sprig.yaml")));
        // Sprig wrote it, so it runs without asking.
        Assert.Equal(Sprig.Engine.Definitions.TrustState.Trusted, new Sprig.Engine.Definitions.FileTrustStore(store.Paths)
            .Check(Path.Combine(api, "sprig.yaml"), Sprig.Engine.Definitions.DefinitionHash.Of(File.ReadAllText(Path.Combine(api, "sprig.yaml")))).State);
    }

    [Fact]
    public void Up_without_an_id_outside_any_listed_repo_says_to_init()
    {
        var (code, _, stderr) = Run("up");

        Assert.Equal(1, code);
        Assert.Contains("sprig init", stderr);
    }

    [Fact]
    public void A_bad_value_for_set_is_refused_before_anything_runs()
    {
        var (code, _, stderr) = Run("up", "shop-web", "--set", "no-equals-sign");

        Assert.Equal(1, code);
        Assert.Contains("isn't name=value", stderr);
    }
}
