using Sprig.Cli;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

[Collection("cli-console")]
public class AgentCommandsTests
{
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

    static string Repo(TempStore store, string yaml)
    {
        var dir = Path.Combine(store.Root, "src", "shop-services");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sprig.yaml"), yaml);
        return dir;
    }

    [Fact]
    public void Check_passes_a_good_repo_file_without_approving_or_registering_it()
    {
        using var store = new TempStore();
        var dir = Repo(store, """
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
            """);

        var (code, stdout, _) = Run(store, "check", dir, "--json");

        Assert.Equal(0, code);
        Assert.Contains("\"ok\": true", stdout);
        Assert.Contains("\"kind\": \"repo\"", stdout);
        Assert.Empty(new FileTrustStore(store.Paths).List());
        Assert.Empty(new StoredDefinitions(store.Paths).Repos());
    }

    [Fact]
    public void Check_names_the_line_of_a_reference_that_resolves_to_nothing()
    {
        using var store = new TempStore();
        var dir = Repo(store, """
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
                  PORT: ${api.prot}
            """);

        var (code, stdout, _) = Run(store, "check", Path.Combine(dir, "sprig.yaml"), "--json");

        Assert.Equal(1, code);
        Assert.Contains("\"code\": \"problems\"", stdout);
        Assert.Contains("\"Severity\": \"Error\"", stdout);
        Assert.Contains("prot", stdout);
        Assert.Contains("\"Line\": 12", stdout);
    }

    [Fact]
    public void Check_reports_an_unknown_key_by_line_and_without_json_too()
    {
        using var store = new TempStore();
        var dir = Repo(store, "schema: 1\nrepo: shop-services\nappz: {}\n");

        var (code, stdout, _) = Run(store, "check", dir);

        Assert.Equal(1, code);
        Assert.Contains("sprig.yaml:3", stdout);
        Assert.Contains("'appz'", stdout);
    }

    [Fact]
    public void Check_without_a_definition_says_how_to_draft_one()
    {
        using var store = new TempStore();
        var empty = Path.Combine(store.Root, "empty");
        Directory.CreateDirectory(empty);

        var (code, stdout, _) = Run(store, "check", empty, "--json");

        Assert.Equal(1, code);
        Assert.Contains("\"code\": \"error\"", stdout);
        Assert.Contains("sprig init", stdout);
    }

    [Theory]
    [InlineData("format", "# Definition format")]
    [InlineData("agent", "# Sprig for agents")]
    public void Docs_prints_the_docs_this_build_carries(string topic, string heading)
    {
        using var store = new TempStore();
        var (code, stdout, _) = Run(store, "docs", topic);

        Assert.Equal(0, code);
        Assert.StartsWith(heading, stdout);
    }

    [Fact]
    public void Schema_prints_the_same_schema_the_site_publishes()
    {
        using var store = new TempStore();
        var (code, stdout, _) = Run(store, "schema");

        Assert.Equal(0, code);
        Assert.Equal(DefinitionSchema.Repo(), stdout);
    }

    [Fact]
    public void Each_reason_a_workspace_fails_has_its_own_code()
    {
        var ask = new PlannedAsk("shop-services", "api-key", "secret", new Sprig.Model.SourceLocation("sprig.yaml", 3));
        var problem = new PlanProblem(ProblemSeverity.Error, "bad", null);

        Assert.Equal("inputs-needed", CliErrors.Code(WorkspaceException.NeedsValues([ask])));
        Assert.Equal("problems", CliErrors.Code(new WorkspaceException("x", problems: [problem])));
        Assert.Equal("slot-broken", CliErrors.Code(new WorkspaceException("x") { Code = CliErrors.SlotBroken }));
        Assert.Equal("error", CliErrors.Code(new InvalidOperationException("x")));
    }
}
