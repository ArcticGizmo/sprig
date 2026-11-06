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
        File.WriteAllText(Path.Combine(web, "sprig.cs"), "// shop-web's own");
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
        var orchestration = Path.Combine(store.Root, "src", "shop.orchestration.cs");
        File.WriteAllText(orchestration, "var orchestration = Orchestration.Describe(\"shop\");\norchestration.UseRepo(\"web-sprig\");\nreturn orchestration.Emit(args);\n");
        new Sprig.Engine.Orchestrations.OrchestrationRegistry(store.Paths).Add("shop", orchestration);
        var (refused, _, why) = Run(store, "repo", "rm", "web-sprig");
        Assert.Equal(1, refused);
        Assert.Contains("used by shop", why);
        Assert.Equal(0, Run(store, "repo", "rm", "web-sprig", "--force").code);
        Assert.Null(stored.Repo("web-sprig"));
        Assert.True(File.Exists(Path.Combine(web, "sprig.cs")));

        // --in-sprig keeps a copy of the folder's own file, even with that file already listed.
        Assert.Equal(0, Run(store, "repo", "add", web, "--in-sprig").code);
        Assert.Equal("// shop-web's own", File.ReadAllText(stored.Repo("shop-web-sprig")!.DefinitionFile));
    }
}
