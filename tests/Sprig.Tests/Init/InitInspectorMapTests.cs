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
        Assert.True(cap.Outputs["port"].IsPort);
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

        Assert.Contains(Provides(p), c => c.Capability == "postgres" && c.Outputs["port"].IsPort);
        var overrides = Assert.Single(Compose(p)).Overrides;
        Assert.Contains(overrides, o => o.Template == "${sprig.postgres.port}:5432");
        Assert.Contains(overrides, o => o.Template.Contains("db--${sprig.workspace}"));
        Assert.True(SprigConfigValidator.Validate(p.Config).IsValid);
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
}
