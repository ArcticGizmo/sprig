using System.Text.Json;
using System.Text.RegularExpressions;
using Sprig.Cli.Commands;

namespace Sprig.Tests.Cli;

/// <summary>
/// The Claude Code plugins under plugins/ ship from this repo but update apart from sprig, so what keeps them in step is
/// checked here: each carries the release's version, each hook passes a contract sprig serves, and the marketplace lists
/// every plugin and nothing else.
/// </summary>
public partial class PluginsTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "sprig.slnx"))) return dir.FullName;
        throw new InvalidOperationException("the repo's root wasn't found above the test output");
    }

    static IEnumerable<string> Plugins() => Directory.GetDirectories(Path.Combine(Root, "plugins")).Order();

    static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    [GeneratedRegex("<Version>([^<]+)</Version>")]
    private static partial Regex CsprojVersion();

    [Fact]
    public void Every_plugin_carries_the_version_sprig_is_released_as()
    {
        var csproj = File.ReadAllText(Path.Combine(Root, "src", "Sprig.App", "Sprig.App.csproj"));
        var version = CsprojVersion().Match(csproj).Groups[1].Value;

        Assert.NotEmpty(Plugins());
        foreach (var plugin in Plugins())
        {
            var manifest = Json(Path.Combine(plugin, ".claude-plugin", "plugin.json"));
            Assert.Equal(Path.GetFileName(plugin), manifest.GetProperty("name").GetString());
            Assert.True(version == manifest.GetProperty("version").GetString(), $"{Path.GetFileName(plugin)} isn't at {version}");
        }
    }

    [Fact]
    public void Every_hook_runs_sprig_hook_with_a_contract_sprig_serves()
    {
        var hooks = Plugins().Select(p => Path.Combine(p, "hooks", "hooks.json")).Where(File.Exists).ToArray();

        Assert.NotEmpty(hooks);
        foreach (var file in hooks)
            foreach (var hookEvent in Json(file).GetProperty("hooks").EnumerateObject())
                foreach (var hook in hookEvent.Value.EnumerateArray().SelectMany(m => m.GetProperty("hooks").EnumerateArray()))
                {
                    Assert.Equal("sprig", hook.GetProperty("command").GetString());
                    var args = hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
                    Assert.Equal("hook", args[0]);
                    Assert.Contains(args[1], HookCommand.Hooks);
                    var contract = int.Parse(args[Array.IndexOf(args, "--contract") + 1]);
                    Assert.True(HookCommand.Contracts.Contains(contract), $"{file} passes contract {contract}, which sprig doesn't serve");
                }
    }

    [Fact]
    public void The_marketplace_lists_each_plugin_once_from_its_own_folder()
    {
        var listed = Json(Path.Combine(Root, ".claude-plugin", "marketplace.json")).GetProperty("plugins").EnumerateArray()
            .Select(p => (name: p.GetProperty("name").GetString()!, source: p.GetProperty("source").GetString()!))
            .ToArray();

        Assert.Equal(Plugins().Select(Path.GetFileName), listed.Select(p => p.name).Order());
        Assert.All(listed, p => Assert.Equal($"./plugins/{p.name}", p.source));
    }
}
