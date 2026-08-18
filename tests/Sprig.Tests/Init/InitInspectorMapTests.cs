using Sprig.Core.Config;
using Sprig.Core.Init;

namespace Sprig.Tests.Init;

/// <summary>M6 — `sprig init --map` proposes the map model: a detected listen/service port becomes a
/// PROVIDED capability the repo owns, and env/compose values are rewritten to ${sprig.&lt;cap&gt;.port}.</summary>
public class InitInspectorMapTests : IDisposable
{
    static IReadOnlyList<ProvidedCapability> Provides(InitProposal p) =>
        p.Config.EffectiveModules.SelectMany(m => m.Provides).ToList();
    static IReadOnlyList<EnvOverride> Env(InitProposal p) => p.Config.EffectiveModules.SelectMany(m => m.Env).ToList();
    static IReadOnlyList<ComposeConfig> Compose(InitProposal p) => p.Config.EffectiveModules.SelectMany(m => m.Compose).ToList();

    readonly string _repo = Path.Combine(Path.GetTempPath(), "sprig-initmap-" + Guid.NewGuid().ToString("N"));
    readonly FakeGitService _git = new();

    public InitInspectorMapTests() => Directory.CreateDirectory(_repo);
    public void Dispose() { try { Directory.Delete(_repo, recursive: true); } catch { } }

    void Write(string file, string content)
    {
        var path = Path.Combine(_repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    InitProposal InspectMap() => new InitInspector(_git).InspectMap(_repo);

    [Fact]
    public void A_bare_port_env_key_becomes_a_provided_capability()
    {
        Write(".env", "PORT=6010\nOTHER=hello\n");
        var p = InspectMap();

        var cap = Assert.Single(Provides(p));
        Assert.True(cap.Ports.ContainsKey("port"));
        Assert.Equal($"${{sprig.{cap.Capability}.port}}", Env(p)[0].Set["PORT"]);
        Assert.True(SprigConfigValidator.Validate(p.Config).IsValid);
    }

    [Fact]
    public void A_compose_service_port_becomes_a_provided_capability_named_after_the_service()
    {
        Write("docker-compose.yml", """
            services:
              postgres:
                container_name: db
                ports:
                  - "6050:5432"
            """);
        var p = InspectMap();

        Assert.Contains(Provides(p), c => c.Capability == "postgres" && c.Ports.ContainsKey("port"));
        var overrides = Assert.Single(Compose(p)).Overrides;
        Assert.Contains(overrides, o => o.Template == "${sprig.postgres.port}:5432");
        Assert.Contains(overrides, o => o.Template.Contains("db--${sprig.workspace}"));
        Assert.True(SprigConfigValidator.Validate(p.Config).IsValid);
    }

    [Fact]
    public void A_committed_port_with_nowhere_gitignored_to_override_is_left_alone_with_a_note()
    {
        // Port lives in a committed .env, and .env.local is NOT gitignored — there's nowhere safe to write,
        // so sprig never clobbers the tracked file; it reports a note instead.
        Write(".env", "PORT=6010\n");
        _git.TrackedFiles.Add(".env");

        var p = InspectMap();

        Assert.Empty(Provides(p));
        Assert.Empty(Env(p));
        Assert.Contains(p.Notes, n => n.Contains("gitignore"));
    }

    [Fact]
    public void A_committed_port_is_overridden_in_a_gitignored_env_local()
    {
        // The common SPA shape: VITE_PORT committed in .env, .env.local gitignored (not yet created).
        // Reading the tracked .env is fine; the override lands in a fresh, gitignored .env.local.
        Write(".env", "VITE_PORT=3000\n");
        _git.TrackedFiles.Add(".env");
        _git.IgnoredFiles.Add(".env.local");

        var p = InspectMap();

        var cap = Assert.Single(Provides(p));
        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);
        Assert.Equal($"${{sprig.{cap.Capability}.port}}", env.Set["VITE_PORT"]);
    }

    [Fact]
    public void Ports_in_a_tracked_sibling_are_detected_and_overridden_in_the_untracked_target()
    {
        Write(".env", "SHARED_PORT=6010\n");   // committed — read for its port, never written
        Write(".env.local", "PORT=7010\n");    // gitignored override — the write target
        _git.TrackedFiles.Add(".env");

        var p = InspectMap();

        // Both ports are found (reading the tracked .env is fine) and both are rewritten in the untracked file.
        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);
        Assert.True(env.Set.ContainsKey("PORT"));
        Assert.True(env.Set.ContainsKey("SHARED_PORT"));
        Assert.Equal(2, Provides(p).Count);
    }

    [Fact]
    public void A_capability_in_a_subdirectory_is_named_after_the_app_folder()
    {
        // A monorepo app: port committed in apps/client/.env, override in a gitignored apps/client/.env.local.
        // Name the capability after the service folder ('client'), not the socket ('vite-port').
        Write("apps/client/.env", "VITE_PORT=3000\n");
        _git.TrackedFiles.Add("apps/client/.env");
        _git.IgnoredFiles.Add("apps/client/.env.local");

        var p = InspectMap();

        var cap = Assert.Single(Provides(p));
        Assert.Equal("client", cap.Capability);
        Assert.Equal("apps/client/.env.local", Assert.Single(Env(p)).File);
    }

    [Fact]
    public void Sample_env_files_are_never_chosen_as_the_override_target()
    {
        // A gitignored real file beside committed/sample siblings — only the real one is picked.
        Write(".env.local", "PORT=5173\n");
        Write(".env.local.template", "PORT=5173\n");
        Write(".env.example", "PORT=5173\n");

        var p = InspectMap();

        var cap = Assert.Single(Provides(p));                 // exactly one capability, from .env.local
        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);
    }

    [Fact]
    public void Only_one_env_file_per_directory_is_chosen()
    {
        // Two runtime candidates at the same level → pick one (.env.local wins over .env), never both.
        Write(".env", "PORT=5173\n");
        Write(".env.local", "PORT=5173\n");

        var p = InspectMap();

        Assert.Single(Provides(p));                           // not two capabilities for the same port
        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);
    }

    [Fact]
    public void A_port_key_names_the_capability_without_the_port_suffix()
    {
        Write(".env.local", "VITE_PORT=5173\n");   // untracked target carrying a port key

        var p = InspectMap();

        var cap = Assert.Single(Provides(p));
        Assert.Equal("vite", cap.Capability);       // VITE_PORT -> vite (referenced as vite.port), not vite-port
        Assert.Equal("${sprig.vite.port}", Env(p)[0].Set["VITE_PORT"]);
    }

    [Fact]
    public void An_embedded_url_is_surfaced_as_a_need_note_not_a_provide()
    {
        Write(".env", "VITE_API_URL=http://localhost:4000\n");
        var p = InspectMap();

        Assert.Empty(Provides(p));   // consuming another service is a need, not a provide
        Assert.Contains(p.Notes, n => n.Contains("VITE_API_URL") && n.Contains("need"));
    }

    [Fact]
    public void A_bare_repo_proposes_nothing_and_says_so()
    {
        var p = InspectMap();
        Assert.Empty(p.Config.EffectiveModules);
        Assert.Contains(p.Notes, n => n.Contains("by hand"));
    }

    [Fact]
    public void Multiple_modules_each_get_their_own_provides_scoped_to_their_path()
    {
        Write("apps/web/.env.local", "PORT=3000\n");
        Write("services/api/.env.local", "PORT=5000\n");

        var p = new InitInspector(_git).InspectMap(_repo,
            [new ModuleSpec("web", "apps/web"), new ModuleSpec("api", "services/api")]);

        var mods = p.Config.EffectiveModules;
        Assert.Equal(["web", "api"], mods.Select(m => m.Name));

        // Each module owns a provided capability, and its env file is stored relative to its own path.
        var web = mods.Single(m => m.Name == "web");
        Assert.Single(web.Provides);
        Assert.Equal(".env.local", web.Env.Single().File);
        Assert.Equal($"${{sprig.{web.Provides[0].Capability}.port}}", web.Env.Single().Set["PORT"]);

        var api = mods.Single(m => m.Name == "api");
        Assert.Single(api.Provides);
        Assert.Equal(".env.local", api.Env.Single().File);

        Assert.True(SprigConfigValidator.Validate(p.Config).IsValid);
    }

    [Fact]
    public void A_named_module_with_nothing_to_detect_is_still_kept()
    {
        // The user asked for it explicitly — keep it so they can declare provides/needs in the editor.
        var p = new InitInspector(_git).InspectMap(_repo, [new ModuleSpec("empty", "apps/empty")]);
        var mod = Assert.Single(p.Config.EffectiveModules);
        Assert.Equal("empty", mod.Name);
        Assert.Empty(mod.Provides);
    }
}
