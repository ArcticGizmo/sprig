using Sprig.Core.Init;

namespace Sprig.Tests.Init;

public class InitInspectorTests : IDisposable
{
    readonly string _repo = Path.Combine(Path.GetTempPath(), "sprig-init-" + Guid.NewGuid().ToString("N"));

    public InitInspectorTests() => Directory.CreateDirectory(_repo);
    public void Dispose() { try { Directory.Delete(_repo, recursive: true); } catch { } }

    void Write(string file, string content) => File.WriteAllText(Path.Combine(_repo, file), content);

    [Fact]
    public void Detects_bare_port_env_key()
    {
        Write(".env", "PORT=6010\nOTHER=hello\n");
        var p = new InitInspector().Inspect(_repo);

        Assert.Single(p.Config.Inputs);
        Assert.Single(p.Config.Env);
        Assert.Equal(".env", p.Config.Env[0].File);
        var input = p.Config.Inputs[0];
        Assert.Equal("6010", input.Example);
        Assert.Equal($"${{sprig.{input.Name}}}", p.Config.Env[0].Set["PORT"]);
    }

    [Fact]
    public void Notes_embedded_port_in_connection_string()
    {
        Write(".env", "ConnectionStrings__Default=Host=localhost;Port=6050;Database=db\n");
        var p = new InitInspector().Inspect(_repo);

        Assert.Empty(p.Config.Inputs); // not a bare int → not auto-detected
        Assert.Contains(p.Notes, n => n.Contains("ConnectionStrings__Default"));
    }

    [Fact]
    public void Detects_compose_service_container_name_and_port()
    {
        Write("docker-compose.yml", """
            services:
              postgres:
                image: postgres:17
                container_name: librarydb_postgres
                ports:
                  - "6050:5432"
            """);
        var p = new InitInspector().Inspect(_repo);

        Assert.NotNull(p.Config.Compose);
        var ovr = p.Config.Compose!.Overrides;
        Assert.Contains(ovr, o => o.Path.SequenceEqual(["services", "postgres", "container_name"])
                                  && o.Template == "librarydb_postgres--${sprig.workspace}");
        Assert.Contains(ovr, o => o.Path.SequenceEqual(["services", "postgres", "ports", "0"])
                                  && o.Template == "${sprig.postgres_port}:5432");
        Assert.Contains(p.Config.Inputs, i => i.Name == "postgres_port" && i.Example == "6050");
    }

    [Fact]
    public void Notes_named_volumes()
    {
        Write("docker-compose.yml", """
            services:
              db:
                image: postgres:17
                volumes:
                  - pgdata:/var/lib/postgresql/data
            volumes:
              pgdata:
            """);
        var p = new InitInspector().Inspect(_repo);
        Assert.Contains(p.Notes, n => n.Contains("named volume") && n.Contains("pgdata"));
    }

    [Fact]
    public void Repo_name_comes_from_folder()
    {
        Write(".env", "PORT=3000\n");
        var p = new InitInspector().Inspect(_repo);
        Assert.Equal(Path.GetFileName(_repo), p.Config.Name);
    }

    [Fact]
    public void Deduplicates_input_names_across_env_and_compose()
    {
        // env key "postgres_port" and a compose service "postgres" (→ postgres_port) would collide
        Write(".env", "postgres_port=6432\n");
        Write("docker-compose.yml", """
            services:
              postgres:
                ports:
                  - "6050:5432"
            """);
        var p = new InitInspector().Inspect(_repo);
        Assert.Equal(p.Config.Inputs.Select(x => x.Name).Distinct().Count(), p.Config.Inputs.Count);
    }

    [Fact]
    public void ParseEnv_skips_comments_and_blanks()
    {
        var pairs = InitInspector.ParseEnv("# comment\n\nA=1\nB = two \n").ToList();
        Assert.Equal([("A", "1"), ("B", "two")], pairs);
    }
}
