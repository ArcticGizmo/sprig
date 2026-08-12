using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Substitution;

namespace Sprig.Tests.Env;

/// <summary>Covers seeding a worktree env file from explicit template file(s).</summary>
public class EnvTemplateSeedTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sprig-envtpl-" + Guid.NewGuid().ToString("N"));
    readonly string _source;
    readonly string _worktree;
    readonly EnvClobberService _svc = new();

    public EnvTemplateSeedTests()
    {
        _source = Path.Combine(_root, "repo");
        _worktree = Path.Combine(_root, "repo--ws");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    static SprigRepoConfig Config(string file, string[] templates, params (string k, string v)[] set) => new()
    {
        Name = "repo",
        Env = [new EnvOverride { File = file, Templates = templates, Set = set.ToDictionary(x => x.k, x => x.v) }],
    };

    static IVariableSource Scope() =>
        SprigScope.ForWorkspace("ws", new Dictionary<string, int> { ["frontend"] = 20001 });

    string WorktreeText(string file) => File.ReadAllText(Path.Combine(_worktree, file)).Replace("\r\n", "\n");

    [Fact]
    public void Seeds_the_target_from_a_template_file_when_the_real_file_is_absent()
    {
        // The real .env.local isn't on this machine; the committed .env.template stands in for it.
        File.WriteAllText(Path.Combine(_source, ".env.template"), "API_KEY=changeme\nOTHER=keep\n");
        var config = Config(".env.local", [".env.template"], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        var text = WorktreeText(".env.local");
        Assert.Contains("API_KEY=changeme", text.Split('\n')); // template content seeded
        Assert.Contains("OTHER=keep", text.Split('\n'));
        Assert.Equal(2, text.Split('\n').Count(l => l == "PORT=20001")); // sprig block top+bottom
    }

    [Fact]
    public void Concatenates_multiple_templates_in_order()
    {
        File.WriteAllText(Path.Combine(_source, ".env.a"), "A=1\n");
        File.WriteAllText(Path.Combine(_source, ".env.b"), "B=2\n");
        var config = Config(".env.local", [".env.a", ".env.b"], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        var lines = WorktreeText(".env.local").Split('\n').ToList();
        Assert.Contains("A=1", lines);
        Assert.Contains("B=2", lines);
        Assert.True(lines.IndexOf("A=1") < lines.IndexOf("B=2")); // order preserved
    }

    [Fact]
    public void Skips_a_missing_template()
    {
        File.WriteAllText(Path.Combine(_source, ".env.a"), "A=1\n");
        var config = Config(".env.local", [".env.a", ".env.missing"], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        var lines = WorktreeText(".env.local").Split('\n');
        Assert.Contains("A=1", lines);
        Assert.Equal(2, lines.Count(l => l == "PORT=20001")); // still written, missing one ignored
    }

    [Fact]
    public void The_real_target_files_values_win_a_shared_key_but_distinct_template_keys_merge()
    {
        // Both the real (gitignored) target file AND a committed template exist. For a key they SHARE,
        // the developer's real value wins (the target has precedence); a key ONLY the template defines
        // is still merged in — a template fills gaps, it never overrides.
        File.WriteAllText(Path.Combine(_source, ".env.local"), "SHARED=fromtarget\nONLY_TARGET=1\n");
        File.WriteAllText(Path.Combine(_source, ".env.template"), "SHARED=fromtemplate\nONLY_TEMPLATE=1\n");
        var config = Config(".env.local", [".env.template"], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        var lines = WorktreeText(".env.local").Split('\n');
        Assert.Contains("SHARED=fromtarget", lines);          // target wins the shared key
        Assert.DoesNotContain("SHARED=fromtemplate", lines);  // the template's value for it is dropped
        Assert.Contains("ONLY_TARGET=1", lines);
        Assert.Contains("ONLY_TEMPLATE=1", lines);            // distinct template key merged in
    }

    [Fact]
    public void An_earlier_template_wins_a_shared_key_over_a_later_one()
    {
        // No real target file — the two templates merge in order, and the first to define a key wins it.
        File.WriteAllText(Path.Combine(_source, ".env.a"), "SHARED=fromA\nONLY_A=1\n");
        File.WriteAllText(Path.Combine(_source, ".env.b"), "SHARED=fromB\nONLY_B=1\n");
        var config = Config(".env.local", [".env.a", ".env.b"], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        var lines = WorktreeText(".env.local").Split('\n');
        Assert.Contains("SHARED=fromA", lines);
        Assert.DoesNotContain("SHARED=fromB", lines);
        Assert.Contains("ONLY_A=1", lines);
        Assert.Contains("ONLY_B=1", lines);
    }

    [Fact]
    public void Falls_back_to_the_template_when_the_target_file_is_present_but_empty()
    {
        // A stub file with no real values (just whitespace) is as good as absent — use the template.
        File.WriteAllText(Path.Combine(_source, ".env.local"), "   \n");
        File.WriteAllText(Path.Combine(_source, ".env.template"), "FROM_TEMPLATE=1\n");
        var config = Config(".env.local", [".env.template"], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        Assert.Contains("FROM_TEMPLATE=1", WorktreeText(".env.local").Split('\n'));
    }

    [Fact]
    public void Empty_templates_list_falls_back_to_the_target_file()
    {
        File.WriteAllText(Path.Combine(_source, ".env"), "OTHER=keep\n");
        var config = Config(".env", [], ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        Assert.Contains("OTHER=keep", WorktreeText(".env").Split('\n'));
    }
}
