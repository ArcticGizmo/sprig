using Sprig.Core.Config;
using Sprig.Core.Git;
using Sprig.Core.Maps;
using Sprig.Core.Processes;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Tests.Examples;

/// <summary>
/// The examples/ sandbox content is authored to the same schema as a user's repos and maps, so a schema
/// change can quietly break it. These run every example through the real loader, validator, registry, map
/// import and resolver, so that breakage fails here rather than in scripts/sandbox.
/// </summary>
public class ExamplesTests
{
    static readonly string ExamplesDir = FindExamples();

    static string FindExamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples");
            if (Directory.Exists(Path.Combine(candidate, "repos"))) return candidate;
        }
        throw new DirectoryNotFoundException("examples/ not found above the test output directory");
    }

    public static TheoryData<string> Repos()
        => new(Directory.GetDirectories(Path.Combine(ExamplesDir, "repos")).Select(Path.GetFileName)!);

    public static TheoryData<string> Maps()
        => new(Directory.GetFiles(Path.Combine(ExamplesDir, "maps"), "*.json").Select(Path.GetFileNameWithoutExtension)!);

    [Theory]
    [MemberData(nameof(Repos))]
    public void Each_example_repo_config_is_valid_and_named_after_its_folder(string repo)
    {
        var config = SprigConfigLoader.LoadFromFile(Path.Combine(ExamplesDir, "repos", repo, ".sprig.json"));
        var result = SprigConfigValidator.Validate(config);
        Assert.True(result.IsValid, string.Join("\n", result.Issues.Select(i => $"{i.Path}: {i.Message}")));
        // The sandbox registers each copy under its config name, and the maps refer to repos by that name.
        Assert.Equal(repo, config.Name);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Each_example_map_imports_and_resolves_with_no_gaps(string mapName)
    {
        using var store = new TempStore();
        var registry = new RepoRegistryStore(store.Paths);
        foreach (var repo in Directory.GetDirectories(Path.Combine(ExamplesDir, "repos")))
            registry.Add(repo);
        var maps = new MapStore(store.Paths, registry);

        var map = maps.Import(Path.Combine(ExamplesDir, "maps", mapName + ".json"));
        Assert.Equal(mapName, map.Name);

        var (definition, repos) = new MapResolver(registry, maps, new GitService(new ProcessRunner()), store.Paths).Resolve(mapName, null);
        var ports = CapabilityResolver.PortRequests(repos)
            .Select((request, i) => (request.Name, Port: 8000 + i))
            .ToDictionary(p => p.Name, p => p.Port);
        var resolved = CapabilityResolver.Resolve("check", definition, repos, ports);
        Assert.Empty(resolved.Unsatisfied);
    }
}
