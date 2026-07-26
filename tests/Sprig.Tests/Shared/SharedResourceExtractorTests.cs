using System.Linq;
using Sprig.Core.Config;
using Sprig.Core.Planning;
using Sprig.Core.Shared;

namespace Sprig.Tests.Shared;

/// <summary>
/// Extraction is the primary entry point, because hand-authoring override rules is the fastest way to make
/// a good feature go unused. The judgement it has to get right is <b>which layer to inject at</b> — and
/// where it can't tell, it has to say so rather than guess, since a wrong guess here means four workspaces
/// quietly sharing one database.
/// </summary>
public class SharedResourceExtractorTests
{
    const string ApiConfig = """
        {
          "schema": 2, "name": "dotnet-api",
          "inputs": [ { "name": "port", "example": "5000" }, { "name": "dbPort", "example": "5432" } ],
          "env": [ { "file": ".env", "set": {
              "PORT": "${sprig.port}",
              "ConnectionStrings__Default": "Host=localhost;Port=${sprig.dbPort};Database=librarydb;Username=library;Password=library_pass"
          } } ],
          "compose": [ { "file": "docker-compose.yml", "overrides": [
              { "path": ["services","postgres","container_name"], "template": "librarydb--${sprig.workspace}" },
              { "path": ["services","postgres","ports","0"], "template": "${sprig.dbPort}:5432" }
          ] } ]
        }
        """;

    const string ComposeYaml = """
        services:
          api:
            image: api:latest
            depends_on: [postgres]
          postgres:
            image: postgres:16-alpine
            container_name: librarydb_postgres
            depends_on: [something]
            environment:
              POSTGRES_PASSWORD: library_pass
            ports:
              - "5432:5432"
            volumes:
              - pgdata:/var/lib/postgresql/data
        volumes:
          pgdata:
          uploads:
        """;

    static (SprigRepoConfig Repo, string Root, TempDir Dir) Fixture(string config = ApiConfig,
        string compose = ComposeYaml)
    {
        var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "docker-compose.yml"), compose);
        return (SprigConfigLoader.Parse(config), dir.Path, dir);
    }

    static ExtractionProposal Extract(string config = ApiConfig, string compose = ComposeYaml)
    {
        var (repo, root, dir) = Fixture(config, compose);
        using (dir)
            return SharedResourceExtractor.Propose(repo, root, "docker-compose.yml", "postgres");
    }

    [Fact]
    public void The_name_comes_from_the_image_so_two_versions_dont_become_one_pool()
    {
        Assert.Equal("postgres-16", SharedResourcePreset.NameFor("postgres:16-alpine"));
        Assert.Equal("postgres-14", SharedResourcePreset.NameFor("postgres:14"));
        Assert.Equal("redis-7", SharedResourcePreset.NameFor("redis:7-alpine"));
        Assert.Equal("postgres", SharedResourcePreset.NameFor("postgres"));
        Assert.Equal("postgres", SharedResourcePreset.NameFor("postgres:latest"));
        Assert.Equal("mysql-8.0", SharedResourcePreset.NameFor("docker.io/library/mysql:8.0"));
    }

    [Fact]
    public void The_postgres_preset_fills_in_values_and_commands()
    {
        var proposal = Extract();

        Assert.Equal("postgres-16", proposal.Resource.Name);
        Assert.Equal("postgres", proposal.Resource.ExecService);
        Assert.Equal("sprig_${sprig.workspace}", proposal.Resource.Values["database"]);
        Assert.Contains("CREATE DATABASE", Assert.Single(proposal.Resource.Attach));
        Assert.Contains("DROP DATABASE", Assert.Single(proposal.Resource.Detach));
        Assert.Empty(SharedResourceStore.Validate(proposal.Resource));
    }

    [Fact]
    public void The_port_input_is_read_from_the_repos_own_override_not_guessed_from_a_name()
    {
        var proposal = Extract();

        var inject = Assert.Single(proposal.Resource.Injects);
        Assert.Equal("${sprig.shared.port}", inject.Inputs["dbPort"]);

        var choice = Assert.Single(proposal.Choices, c => c.Target == PlanTargets.Input("dbPort"));
        Assert.Equal(PlanLayer.Stack, choice.Layer);
        Assert.Contains("already declares the input", choice.Why);
    }

    [Fact]
    public void A_pinned_database_name_is_rewritten_at_the_env_layer_and_says_why()
    {
        var proposal = Extract();

        var env = Assert.Single(Assert.Single(proposal.Resource.Injects).Env);
        Assert.Equal(".env", env.File);
        Assert.Equal(
            "Host=localhost;Port=${sprig.dbPort};Database=${sprig.shared.database};" +
            "Username=${sprig.shared.user};Password=${sprig.shared.password}",
            env.Set["ConnectionStrings__Default"]);

        var choice = Assert.Single(proposal.Choices,
            c => c.Target == PlanTargets.EnvKey(".env", "ConnectionStrings__Default"));
        Assert.Equal(PlanLayer.Repo, choice.Layer);
        Assert.Contains("one layer deeper", choice.Why);
        Assert.Empty(proposal.Warnings);
    }

    // Keys that don't reference the service are none of extraction's business.
    [Fact]
    public void An_unrelated_env_key_is_left_alone()
    {
        var env = Assert.Single(Assert.Single(Extract().Resource.Injects).Env);
        Assert.DoesNotContain("PORT", env.Set.Keys);
    }

    // The failure this whole step exists to prevent: four workspaces on one server, all writing to the
    // same database. If sprig can't see where the name is, it must say so rather than guess.
    [Fact]
    public void A_connection_string_it_cant_read_becomes_a_warning_not_a_guess()
    {
        const string opaque = """
            {
              "schema": 2, "name": "dotnet-api",
              "inputs": [ { "name": "dbPort", "example": "5432" } ],
              "env": [ { "file": ".env", "set": {
                  "DSN": "pg:${sprig.dbPort}:librarydb:library:library_pass"
              } } ],
              "compose": [ { "file": "docker-compose.yml", "overrides": [
                  { "path": ["services","postgres","ports","0"], "template": "${sprig.dbPort}:5432" }
              ] } ]
            }
            """;

        var proposal = Extract(opaque);

        Assert.Empty(Assert.Single(proposal.Resource.Injects).Env);
        var warning = Assert.Single(proposal.Warnings);
        Assert.Contains("DSN", warning);
        Assert.Contains("would use the same one", warning);
    }

    [Theory]
    [InlineData("Host=x;Database=librarydb;User=y", "Host=x;Database=${sprig.shared.database};User=y")]
    [InlineData("Server=x;Initial Catalog=Books", "Server=x;Initial Catalog=${sprig.shared.database}")]
    [InlineData("Database=d;Username=u;Password=p",
        "Database=${sprig.shared.database};Username=${sprig.shared.user};Password=${sprig.shared.password}")]
    [InlineData("postgres://u:p@localhost:5432/librarydb", "postgres://u:p@localhost:5432/${sprig.shared.database}")]
    [InlineData("mongodb://localhost:27017/app_db", "mongodb://localhost:27017/${sprig.shared.database}")]
    public void Recognised_connection_shapes_are_rewritten(string input, string expected)
        => Assert.Equal(expected, SharedResourceExtractor.Rewrite(input));

    [Theory]
    [InlineData("pg:5432:librarydb:u:p")]
    [InlineData("just-a-value")]
    [InlineData("postgres://localhost:5432")]
    public void Unrecognised_shapes_are_left_for_a_human(string input)
        => Assert.Null(SharedResourceExtractor.Rewrite(input));

    [Fact]
    public void The_service_is_suppressed_and_the_reason_recorded()
    {
        var proposal = Extract();

        var suppress = Assert.Single(Assert.Single(proposal.Resource.Injects).Suppress);
        Assert.Equal("docker-compose.yml", suppress.File);
        Assert.Equal(["postgres"], suppress.Services);

        var choice = Assert.Single(proposal.Choices,
            c => c.Target == PlanTargets.ComposeService("docker-compose.yml", "postgres"));
        Assert.Contains("run the same thing twice", choice.Why);
    }

    [Fact]
    public void The_fragment_lifts_the_service_and_the_volume_that_holds_its_data()
    {
        var fragment = Extract().ComposeFragment;

        Assert.Contains("postgres:16-alpine", fragment);
        Assert.Contains("POSTGRES_PASSWORD", fragment);
        Assert.Contains("pgdata", fragment);
        Assert.DoesNotContain("uploads", fragment);       // not this service's volume
        Assert.DoesNotContain("api:latest", fragment);    // the app stays behind

        // The parts that belonged to the repo don't come along: a container_name would collide across
        // workspaces, and the dependency it declared stayed in the file it came from.
        Assert.DoesNotContain("container_name", fragment);
        Assert.DoesNotContain("depends_on", fragment);

        // One address for everybody, at the standard port.
        Assert.Contains("5432:5432", fragment);
    }

    [Fact]
    public void An_image_with_no_preset_still_extracts_but_says_what_is_missing()
    {
        const string compose = """
            services:
              queue:
                image: some-obscure/broker:3
            """;
        const string config = """
            { "schema": 2, "name": "dotnet-api",
              "compose": [ { "file": "docker-compose.yml", "overrides": [] } ] }
            """;

        var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "docker-compose.yml"), compose);
        using (dir)
        {
            var proposal = SharedResourceExtractor.Propose(
                SprigConfigLoader.Parse(config), dir.Path, "docker-compose.yml", "queue");

            Assert.Equal("broker-3", proposal.Resource.Name);
            Assert.Empty(proposal.Resource.Attach);
            Assert.Contains(proposal.Warnings, w => w.Contains("no preset"));
        }
    }

    [Fact]
    public void Extracting_a_service_that_isnt_there_says_so()
    {
        var (repo, root, dir) = Fixture();
        using (dir)
        {
            var ex = Assert.Throws<SharedResourceException>(() =>
                SharedResourceExtractor.Propose(repo, root, "docker-compose.yml", "mongo"));
            Assert.Contains("no service 'mongo'", ex.Message);
        }
    }
}
