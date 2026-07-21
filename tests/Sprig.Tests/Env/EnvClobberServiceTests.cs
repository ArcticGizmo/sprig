using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Substitution;

namespace Sprig.Tests.Env;

public class EnvClobberServiceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sprig-env-" + Guid.NewGuid().ToString("N"));
    readonly string _source;
    readonly string _worktree;
    readonly EnvClobberService _svc = new();

    public EnvClobberServiceTests()
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

    static SprigRepoConfig Config(string file, params (string k, string v)[] set) => new()
    {
        Name = "repo",
        Env = [new EnvOverride { File = file, Set = set.ToDictionary(x => x.k, x => x.v) }],
    };

    static IVariableSource Scope() =>
        SprigScope.ForWorkspace("ws", new Dictionary<string, int> { ["frontend"] = 20001 });

    [Fact]
    public void Seeds_from_source_and_wraps_top_and_bottom()
    {
        File.WriteAllText(Path.Combine(_source, ".env"), "PORT=6010\nOTHER=keep\n");
        var config = Config(".env", ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());

        var text = File.ReadAllText(Path.Combine(_worktree, ".env"));
        var lines = text.Replace("\r\n", "\n").Split('\n');

        Assert.Equal(EnvClobberService.BeginMarker, lines[0]);        // block at top
        Assert.Contains("PORT=20001", lines);                        // resolved value
        Assert.Contains("OTHER=keep", lines);                        // seeded content preserved
        Assert.Equal(EnvClobberService.EndMarker, lines[^2]);        // block at bottom (last real line)
        // The resolved key appears twice (top and bottom blocks).
        Assert.Equal(2, lines.Count(l => l == "PORT=20001"));
    }

    [Fact]
    public void Source_repo_is_never_written()
    {
        var sourceEnv = Path.Combine(_source, ".env");
        File.WriteAllText(sourceEnv, "PORT=6010\n");
        var before = File.ReadAllText(sourceEnv);

        _svc.Apply(Config(".env", ("PORT", "${sprig.ports.frontend}")), _source, _worktree, Scope());

        Assert.Equal(before, File.ReadAllText(sourceEnv));
    }

    [Fact]
    public void Works_when_source_file_absent_writes_only_blocks()
    {
        // No .env.local in source — target gets just the sprig blocks.
        _svc.Apply(Config(".env.local", ("PORT", "${sprig.ports.frontend}")), _source, _worktree, Scope());

        var text = File.ReadAllText(Path.Combine(_worktree, ".env.local"));
        Assert.Equal(2, text.Split('\n').Count(l => l == "PORT=20001"));
    }

    [Fact]
    public void Reapply_is_idempotent_no_duplicate_blocks()
    {
        File.WriteAllText(Path.Combine(_source, ".env"), "PORT=6010\nOTHER=keep\n");
        var config = Config(".env", ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());
        var first = File.ReadAllText(Path.Combine(_worktree, ".env"));
        _svc.Apply(config, _source, _worktree, Scope());
        var second = File.ReadAllText(Path.Combine(_worktree, ".env"));

        Assert.Equal(first, second); // seeding re-strips, so no growth
        Assert.Equal(2, second.Split('\n').Count(l => l == EnvClobberService.BeginMarker));
    }

    [Fact]
    public void Strip_restores_the_seeded_content()
    {
        File.WriteAllText(Path.Combine(_source, ".env"), "PORT=6010\nOTHER=keep\n");
        var config = Config(".env", ("PORT", "${sprig.ports.frontend}"));

        _svc.Apply(config, _source, _worktree, Scope());
        _svc.Strip(config, _worktree);

        Assert.Equal("PORT=6010\nOTHER=keep\n",
            File.ReadAllText(Path.Combine(_worktree, ".env")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Only_targeted_files_are_touched()
    {
        File.WriteAllText(Path.Combine(_source, ".env"), "PORT=6010\n");
        File.WriteAllText(Path.Combine(_source, ".env.production"), "PORT=9999\n");

        _svc.Apply(Config(".env", ("PORT", "${sprig.ports.frontend}")), _source, _worktree, Scope());

        Assert.True(File.Exists(Path.Combine(_worktree, ".env")));
        Assert.False(File.Exists(Path.Combine(_worktree, ".env.production"))); // not targeted → not created
    }
}
