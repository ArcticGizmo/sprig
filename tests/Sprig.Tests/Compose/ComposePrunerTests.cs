using System.Collections.Generic;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Substitution;

namespace Sprig.Tests.Compose;

/// <summary>
/// Suppression has to be a package deal. Removing <c>services.postgres</c> on its own leaves a file docker
/// refuses to bring up, because something still declares <c>depends_on: [postgres]</c> — trading a
/// duplicated container for a broken one.
/// </summary>
public class ComposePrunerTests
{
    const string Yaml = """
        services:
          api:
            image: api:latest
            depends_on:
              - postgres
              - redis
            networks: [backend]
            ports:
              - "5000:5000"
          postgres:
            image: postgres:16-alpine
            volumes:
              - pgdata:/var/lib/postgresql/data
            networks: [backend]
          redis:
            image: redis:7
        volumes:
          pgdata:
          uploads:
        networks:
          backend:
        """;

    const string HealthYaml = """
        services:
          api:
            image: api:latest
            depends_on:
              postgres:
                condition: service_healthy
          postgres:
            image: postgres:16
        """;

    static IVariableSource Scope() => SprigScope.ForValues("feature-x", new Dictionary<string, string>
    {
        ["dbPort"] = "8034",
    });

    static string Generate(string yaml, IReadOnlyList<string>? suppress, ComposeConfig? config = null)
        => new ComposeGenerator().Generate(yaml, config ?? new ComposeConfig { File = "docker-compose.yml" },
            Scope(), suppress);

    [Fact]
    public void Nothing_to_suppress_leaves_the_document_exactly_as_before()
    {
        var untouched = Generate(Yaml, null);
        var explicitlyEmpty = Generate(Yaml, []);

        Assert.Equal(untouched, explicitlyEmpty);
        // Including the volume nothing references — pruning only runs when suppression does, so a repo
        // that pools nothing sees byte-identical output to before the feature existed.
        Assert.Contains("uploads", untouched);
    }

    [Fact]
    public void A_suppressed_service_is_removed_along_with_its_depends_on_entry()
    {
        var result = Generate(Yaml, ["postgres"]);

        Assert.DoesNotContain("postgres:16-alpine", result);
        Assert.DoesNotContain("- postgres", result);
        Assert.Contains("- redis", result);      // the other dependency survives
        Assert.Contains("api:latest", result);
    }

    [Fact]
    public void The_map_form_of_depends_on_is_pruned_too()
    {
        var result = Generate(HealthYaml, ["postgres"]);

        Assert.DoesNotContain("postgres", result);
        Assert.DoesNotContain("service_healthy", result);
        Assert.DoesNotContain("depends_on", result);   // it emptied, so the key goes with it
        Assert.Contains("api:latest", result);
    }

    [Fact]
    public void A_volume_only_the_suppressed_service_used_is_dropped()
    {
        var result = Generate(Yaml, ["postgres"]);

        Assert.DoesNotContain("pgdata", result);
        // `uploads` was already unreferenced before we touched anything. It isn't ours to remove — the
        // repo declared it, and suppression is not a licence to tidy up someone else's file.
        Assert.Contains("uploads", result);
    }

    [Fact]
    public void A_network_still_used_by_a_surviving_service_is_kept()
    {
        var result = Generate(Yaml, ["postgres"]);
        Assert.Contains("backend", result);   // api still declares it
    }

    [Fact]
    public void A_network_left_with_no_users_is_dropped()
    {
        const string yaml = """
            services:
              api:
                image: api:latest
              postgres:
                image: postgres:16
                networks: [dbnet]
            networks:
              dbnet:
            """;

        var result = Generate(yaml, ["postgres"]);

        Assert.DoesNotContain("dbnet", result);
        Assert.DoesNotContain("networks", result);
    }

    [Fact]
    public void Overrides_still_apply_to_the_services_that_survive()
    {
        var config = new ComposeConfig
        {
            File = "docker-compose.yml",
            Overrides = [new ComposeOverride { Path = ["services", "api", "ports", "0"], Template = "${sprig.dbPort}:5000" }],
        };

        var result = Generate(Yaml, ["postgres"], config);

        Assert.Contains("8034:5000", result);
        Assert.DoesNotContain("postgres:16-alpine", result);
    }

    // The repo committed a compose file where postgres has a ports override. Suppressing postgres must
    // not turn that perfectly valid override into a "path not found" — overrides run first, then pruning.
    [Fact]
    public void An_override_that_targets_a_suppressed_service_is_not_an_error()
    {
        var config = new ComposeConfig
        {
            File = "docker-compose.yml",
            Overrides = [new ComposeOverride { Path = ["services", "postgres", "image"], Template = "postgres:15" }],
        };

        var result = Generate(Yaml, ["postgres"], config);

        Assert.DoesNotContain("postgres:15", result);
        Assert.Contains("api:latest", result);
    }

    [Fact]
    public void Suppressing_a_service_that_isnt_there_says_so()
    {
        var ex = Assert.Throws<ComposeException>(() => Generate(Yaml, ["mongo"]));
        Assert.Contains("no service 'mongo'", ex.Message);
        Assert.Contains("renamed or removed", ex.Message);
    }

    [Fact]
    public void A_file_whose_every_service_is_suppressed_is_not_written()
    {
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "docker-compose.yml");
        File.WriteAllText(source, """
            services:
              postgres:
                image: postgres:16
            volumes:
              pgdata:
            """);
        var dest = Path.Combine(dir.Path, "out", "generated.yml");

        var written = new ComposeGenerator().GenerateToFile(
            source, new ComposeConfig { File = "docker-compose.yml" }, Scope(), dest, ["postgres"]);

        Assert.Null(written);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void A_file_with_a_survivor_is_written_and_its_path_returned()
    {
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "docker-compose.yml");
        File.WriteAllText(source, Yaml);
        var dest = Path.Combine(dir.Path, "out", "generated.yml");

        var written = new ComposeGenerator().GenerateToFile(
            source, new ComposeConfig { File = "docker-compose.yml" }, Scope(), dest, ["postgres"]);

        Assert.Equal(dest, written);
        Assert.Contains("api:latest", File.ReadAllText(dest));
    }
}

/// <summary>A throwaway directory for tests that need real files on disk.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "sprig-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
