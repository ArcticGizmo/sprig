using Sprig.Core.Config;
using Sprig.Core.Init;

namespace Sprig.Tests.Init;

public class InitInspectorTests : IDisposable
{
    // init proposes a single default module; these expose its env/compose for the assertions (empty
    // when nothing was detected). Keeps the tests focused on detection, not the module wrapper.
    static IReadOnlyList<EnvOverride> Env(InitProposal p) => p.Config.EffectiveModules.SelectMany(m => m.Env).ToList();
    static IReadOnlyList<ComposeConfig> Compose(InitProposal p) => p.Config.EffectiveModules.SelectMany(m => m.Compose).ToList();

    readonly string _repo = Path.Combine(Path.GetTempPath(), "sprig-init-" + Guid.NewGuid().ToString("N"));
    readonly FakeGitService _git = new();

    public InitInspectorTests() => Directory.CreateDirectory(_repo);
    public void Dispose() { try { Directory.Delete(_repo, recursive: true); } catch { } }

    void Write(string file, string content)
    {
        var path = Path.Combine(_repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Mark repo-relative (forward-slash) paths as git-tracked for the inspection.</summary>
    void Track(params string[] files) => _git.TrackedFiles.AddRange(files);

    InitProposal Inspect() => new InitInspector(_git).Inspect(_repo);

    [Fact]
    public void Detects_bare_port_env_key()
    {
        Write(".env", "PORT=6010\nOTHER=hello\n");
        var p = Inspect();

        Assert.Single(p.Config.Inputs);
        Assert.Single(Env(p));
        Assert.Equal(".env", Env(p)[0].File);
        var input = p.Config.Inputs[0];
        Assert.Equal("6010", input.Example);
        Assert.Equal($"${{sprig.{input.Name}}}", Env(p)[0].Set["PORT"]);
    }

    [Fact]
    public void Notes_embedded_port_in_connection_string()
    {
        Write(".env", "ConnectionStrings__Default=Host=localhost;Port=6050;Database=db\n");
        var p = Inspect();

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
        var p = Inspect();

        var compose = Assert.Single(Compose(p));
        Assert.Equal("docker-compose.yml", compose.File);
        var ovr = compose.Overrides;
        Assert.Contains(ovr, o => o.Path.SequenceEqual(["services", "postgres", "container_name"])
                                  && o.Template == "librarydb_postgres--${sprig.workspace}");
        Assert.Contains(ovr, o => o.Path.SequenceEqual(["services", "postgres", "ports", "0"])
                                  && o.Template == "${sprig.postgres_port}:5432");
        Assert.Contains(p.Config.Inputs, i => i.Name == "postgres_port" && i.Example == "6050");
    }

    [Fact]
    public void Detects_compose_files_recursively_across_subdirectories()
    {
        Write("docker-compose.yml", """
            services:
              api:
                ports:
                  - "5000:5000"
            """);
        Write("apps/web/compose.yaml", """
            services:
              web:
                ports:
                  - "3000:3000"
            """);
        // Inside an excluded dir — must be ignored.
        Write("node_modules/pkg/docker-compose.yml", """
            services:
              junk:
                ports:
                  - "9999:9999"
            """);

        var p = Inspect();

        Assert.Equal(["apps/web/compose.yaml", "docker-compose.yml"],
            Compose(p).Select(c => c.File).OrderBy(f => f));
        Assert.Contains(p.Config.Inputs, i => i.Name == "api_port" && i.Example == "5000");
        Assert.Contains(p.Config.Inputs, i => i.Name == "web_port" && i.Example == "3000");
        Assert.DoesNotContain(p.Config.Inputs, i => i.Example == "9999");
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
        var p = Inspect();
        Assert.Contains(p.Notes, n => n.Contains("named volume") && n.Contains("pgdata"));
    }

    [Fact]
    public void Repo_name_comes_from_folder()
    {
        Write(".env", "PORT=3000\n");
        var p = Inspect();
        Assert.Equal(Path.GetFileName(_repo), p.Config.Name);
    }

    [Fact]
    public void Proposes_schema_3_with_a_single_default_module()
    {
        Write(".env", "PORT=3000\n");
        var p = Inspect();

        Assert.Equal(3, p.Config.Schema);
        var module = Assert.Single(p.Config.Modules);
        Assert.Equal("app", module.Name);
        Assert.Equal("", module.Path);
        Assert.Null(p.Config.Env);   // nothing at the legacy top level
    }

    [Fact]
    public void Proposes_no_modules_when_nothing_is_detected()
    {
        var p = Inspect();
        Assert.Empty(p.Config.Modules);
        Assert.Equal(3, p.Config.Schema);
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
        var p = Inspect();
        Assert.Equal(p.Config.Inputs.Select(x => x.Name).Distinct().Count(), p.Config.Inputs.Count);
    }

    [Fact]
    public void ParseEnv_skips_comments_and_blanks()
    {
        var pairs = InitInspector.ParseEnv("# comment\n\nA=1\nB = two \n").ToList();
        Assert.Equal([("A", "1"), ("B", "two")], pairs);
    }

    // -- git-aware targeting ---------------------------------------------------

    [Fact]
    public void Skips_tracked_env_and_seeds_the_untracked_file_next_to_it()
    {
        // Classic pattern: .env is a committed template, .env.local is the gitignored real file.
        Write(".env", "PORT=8100\n");
        Write(".env.local", "PORT=8100\n");
        Track(".env");

        var p = Inspect();

        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);                 // the untracked file is the target
        Assert.Equal([".env"], env.Templates);                // the tracked variant seeds it
        Assert.Single(p.Config.Inputs);                       // PORT deduped across target + template
        Assert.Equal($"${{sprig.{p.Config.Inputs[0].Name}}}", env.Set["PORT"]);
    }

    [Fact]
    public void Tracked_template_ports_are_isolated_even_when_target_is_empty()
    {
        // The runtime file exists (untracked) but declares no port; the port lives in the template.
        Write(".env", "PORT=8100\n");
        Write(".env.local", "# nothing yet\n");
        Track(".env");

        var p = Inspect();

        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);
        Assert.Equal([".env"], env.Templates);
        Assert.True(env.Set.ContainsKey("PORT"));
        Assert.Equal("8100", Assert.Single(p.Config.Inputs).Example);
    }

    [Fact]
    public void Tracked_template_without_an_untracked_target_seeds_nothing()
    {
        // Only a committed .env exists — there is no safe file to override, so propose no env block.
        Write(".env", "PORT=8100\n");
        Track(".env");

        var p = Inspect();

        Assert.Empty(Env(p));
        Assert.Empty(p.Config.Inputs);
    }

    [Fact]
    public void Recurses_into_subdirectories()
    {
        Write("apps/web/.env.local", "PORT=3000\n");
        var p = Inspect();

        var env = Assert.Single(Env(p));
        Assert.Equal("apps/web/.env.local", env.File);
    }

    [Fact]
    public void Templates_pair_only_within_the_same_directory()
    {
        // A tracked .env in the root must not seed an untracked file in a subdirectory.
        Write(".env", "ROOT_PORT=9000\n");
        Track(".env");
        Write("api/.env.local", "PORT=3000\n");

        var p = Inspect();

        var env = Assert.Single(Env(p));
        Assert.Equal("api/.env.local", env.File);
        Assert.Null(env.Templates);   // nothing tracked next to it
    }

    [Fact]
    public void Skips_build_and_dependency_directories()
    {
        Write("node_modules/pkg/.env", "PORT=9999\n");
        Write("dist/.env", "PORT=9998\n");
        Write("obj/.env", "PORT=9997\n");

        var p = Inspect();

        Assert.Empty(Env(p));
    }

    [Fact]
    public void Untracked_env_without_a_port_is_not_seeded()
    {
        Write(".env.local", "NAME=hello\nDEBUG=true\n");
        var p = Inspect();

        Assert.Empty(Env(p));
    }

    // -- explicit multi-module scaffolding -------------------------------------

    InitProposal Inspect(params ModuleSpec[] modules) => new InitInspector(_git).Inspect(_repo, modules);

    [Fact]
    public void Scopes_detection_to_each_module_path_with_module_relative_files()
    {
        Write("apps/web/.env.local", "PORT=3000\n");
        Write("services/api/.env.local", "PORT=5000\n");

        var p = Inspect(new ModuleSpec("web", "apps/web"), new ModuleSpec("api", "services/api"));

        Assert.Equal(["web", "api"], p.Config.Modules.Select(m => m.Name));

        var web = p.Config.Modules[0];
        Assert.Equal("apps/web", web.Path);
        Assert.Equal(".env.local", Assert.Single(web.Env).File);   // stored relative to the module path

        var api = p.Config.Modules[1];
        Assert.Equal("services/api", api.Path);
        Assert.Equal(".env.local", Assert.Single(api.Env).File);
    }

    [Fact]
    public void A_module_only_sees_files_under_its_own_path()
    {
        Write("apps/web/.env.local", "PORT=3000\n");
        Write("services/api/.env.local", "PORT=5000\n");

        // The web module must not pick up the api module's env file.
        var web = Assert.Single(Inspect(new ModuleSpec("web", "apps/web")).Config.Modules);
        var env = Assert.Single(web.Env);
        Assert.Equal(".env.local", env.File);
        Assert.Single(env.Set);   // only web's PORT
    }

    [Fact]
    public void Rebases_compose_file_paths_under_the_module()
    {
        Write("apps/web/docker-compose.yml", """
            services:
              web:
                ports:
                  - "3000:3000"
            """);

        var p = Inspect(new ModuleSpec("web", "apps/web"));
        var web = Assert.Single(p.Config.Modules);
        Assert.Equal("docker-compose.yml", Assert.Single(web.Compose).File);
        Assert.Contains(p.Config.Inputs, i => i.Name == "web_port" && i.Example == "3000");
    }

    [Fact]
    public void Keeps_a_defined_module_even_when_its_path_yields_nothing()
    {
        Write("apps/web/.env.local", "PORT=3000\n");

        // The api path has no detectable surface (and doesn't even exist) — it's still created, empty.
        var proposal = Inspect(new ModuleSpec("web", "apps/web"), new ModuleSpec("api", "services/api"));

        Assert.Equal(["web", "api"], proposal.Config.Modules.Select(m => m.Name));
        var api = proposal.Config.Modules[1];
        Assert.Empty(api.Env);
        Assert.Empty(api.Compose);
    }

    [Fact]
    public void Shares_and_deduplicates_inputs_across_modules()
    {
        // Both modules expose a "postgres" compose service → both want the input name "postgres_port".
        Write("a/docker-compose.yml", """
            services:
              postgres:
                ports:
                  - "6001:5432"
            """);
        Write("b/docker-compose.yml", """
            services:
              postgres:
                ports:
                  - "6002:5432"
            """);

        var p = Inspect(new ModuleSpec("a", "a"), new ModuleSpec("b", "b"));

        // Names are unique repo-wide (shared dedup), and there are two of them.
        Assert.Equal(2, p.Config.Inputs.Count);
        Assert.Equal(p.Config.Inputs.Select(i => i.Name).Distinct().Count(), p.Config.Inputs.Count);
    }

    [Fact]
    public void Empty_module_list_falls_back_to_the_single_default_module()
    {
        Write(".env", "PORT=6010\n");
        var p = new InitInspector(_git).Inspect(_repo, []);

        var module = Assert.Single(p.Config.Modules);
        Assert.Equal("app", module.Name);
        Assert.Equal("", module.Path);
    }
}
