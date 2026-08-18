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
    public void A_tracked_env_file_is_left_alone_by_auto_detection()
    {
        // A committed .env is a shared default sprig must never clobber — auto-detection ignores it entirely.
        Write(".env", "PORT=6010\n");
        _git.TrackedFiles.Add(".env");

        var p = InspectMap();

        Assert.Empty(Provides(p));
        Assert.Empty(Env(p));
    }

    [Fact]
    public void Only_the_untracked_env_file_is_scanned_when_a_tracked_one_sits_beside_it()
    {
        Write(".env", "SHARED_PORT=6010\n");   // tracked default — not scanned, not seeded as a template
        Write(".env.local", "PORT=7010\n");    // gitignored override — the only detection source
        _git.TrackedFiles.Add(".env");

        var p = InspectMap();

        Assert.Single(Provides(p));            // only the untracked PORT became a capability
        var env = Assert.Single(Env(p));
        Assert.Equal(".env.local", env.File);
        Assert.True(env.Set.ContainsKey("PORT"));
        Assert.False(env.Set.ContainsKey("SHARED_PORT"));   // the tracked file was not scanned
        Assert.Null(env.Templates);                          // and not attached as a template
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
