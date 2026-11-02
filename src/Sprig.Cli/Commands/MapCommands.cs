using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Maps;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig map <sub>` — map management only. Maps are authored in the app; workspaces come from `pool checkout`
// (pooled) or `ws create --map` (ad-hoc), so there is no `map create`.

[Description("List defined maps")]
public sealed class MapLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var maps = cli.Maps.List();
        if (s.Json) { CliOutput.Json(maps.Select(m => new { m.Name, repos = m.Repos.Select(r => r.Name) })); return 0; }
        if (maps.Count == 0) { cli.Ansi.MarkupLine("[dim]no maps defined[/]"); return 0; }

        var table = CliFormat.Table("NAME", "REPOS");
        foreach (var m in maps)
            table.AddRow(Markup.Escape(m.Name), Markup.Escape(string.Join(", ", m.Repos.Select(r => r.Name))));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Import a map definition from a JSON file (validates + saves it)")]
public sealed class MapImportCommand(CliContext cli) : Command<MapImportCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("Path to a map JSON file")]
        public string File { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var map = cli.Maps.Import(s.File);
        return CliOutput.Ok(s.Json, $"imported map '{map.Name}' ({map.Repos.Count} repo{(map.Repos.Count == 1 ? "" : "s")})",
            new { ok = true, name = map.Name, repos = map.Repos.Select(r => r.Name) });
    }
}

public class MapNameSettings : GlobalSettings
{
    [CommandArgument(0, "<map>")]
    [Description("Map name")]
    public string Map { get; set; } = "";
}

[Description("Show how a map's needs are wired: what is met, what isn't, shared services, constants and fixed values")]
public sealed class MapShowCommand(CliContext cli) : Command<MapNameSettings>
{
    protected override int Execute(CommandContext context, MapNameSettings s, CancellationToken cancellation)
    {
        var inspection = new MapInspector(cli.Repos, cli.Maps).Inspect(s.Map);
        if (s.Json) { MapReport.Json(inspection, ok: true); return 0; }
        MapReport.Full(cli.Ansi, inspection);
        return 0;
    }
}

[Description("Exit non-zero when a checkout of the map would stop: unmet or ambiguous needs, or unreadable repos")]
public sealed class MapCheckCommand(CliContext cli) : Command<MapNameSettings>
{
    protected override int Execute(CommandContext context, MapNameSettings s, CancellationToken cancellation)
    {
        var inspection = new MapInspector(cli.Repos, cli.Maps).Inspect(s.Map);
        if (s.Json) { MapReport.Json(inspection, ok: inspection.IsReady); return inspection.IsReady ? 0 : 1; }
        MapReport.Verdict(cli.Ansi, inspection);
        MapReport.Blockers(cli.Ansi, inspection);
        return inspection.IsReady ? 0 : 1;
    }
}

[Description("Set or remove the constant a map gives a need when nothing in the map provides it")]
public sealed class MapDefaultCommand(CliContext cli) : Command<MapDefaultCommand.Settings>
{
    public sealed class Settings : MapNameSettings
    {
        [CommandArgument(1, "<repo>")]
        [Description("The repo whose need gets the constant")]
        public string Repo { get; set; } = "";

        [CommandArgument(2, "<capability>")]
        [Description("The need's capability name")]
        public string Capability { get; set; } = "";

        [CommandArgument(3, "[values]")]
        [Description("One or more output=value pairs, e.g. url=https://staging.example.test")]
        public string[] Values { get; set; } = [];

        [CommandOption("--rm")]
        [Description("Remove the constant instead")]
        public bool Remove { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var editor = new MapEditor(cli.Maps, cli.Repos);
        if (s.Remove)
        {
            if (s.Values.Length > 0) throw new ArgumentException("--rm takes no values");
            editor.RemoveDefault(s.Map, s.Repo, s.Capability);
            return CliOutput.Ok(s.Json, $"removed the constant for {s.Repo}'s '{s.Capability}' from map '{s.Map}'",
                new { ok = true, map = s.Map, repo = s.Repo, capability = s.Capability, removed = true });
        }

        if (s.Values.Length == 0) throw new ArgumentException("give at least one output=value pair (or --rm)");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in s.Values)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"'{pair}' isn't output=value");
            values[pair[..eq].Trim()] = pair[(eq + 1)..];
        }
        editor.SetDefault(s.Map, s.Repo, s.Capability, values);
        return CliOutput.Ok(s.Json,
            $"map '{s.Map}' gives {s.Repo}'s '{s.Capability}' {string.Join(", ", values.Select(v => $"{v.Key} = {v.Value}"))}",
            new { ok = true, map = s.Map, repo = s.Repo, capability = s.Capability, values });
    }
}

[Description("Wire a repo's need to a capability, or to repo/capability to pick between providers")]
public sealed class MapWireCommand(CliContext cli) : Command<MapWireCommand.Settings>
{
    public sealed class Settings : MapNameSettings
    {
        [CommandArgument(1, "<repo>")]
        [Description("The repo whose need is wired")]
        public string Repo { get; set; } = "";

        [CommandArgument(2, "<need>")]
        [Description("The need's capability name")]
        public string Need { get; set; } = "";

        [CommandArgument(3, "[target]")]
        [Description("The capability to wire it to, or provider-repo/capability")]
        public string? Target { get; set; }

        [CommandOption("--rm")]
        [Description("Remove the wiring entry instead")]
        public bool Remove { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var editor = new MapEditor(cli.Maps, cli.Repos);
        if (s.Remove)
        {
            if (s.Target is not null) throw new ArgumentException("--rm takes no target");
            editor.RemoveWiring(s.Map, s.Repo, s.Need);
            return CliOutput.Ok(s.Json, $"removed the wiring for {s.Repo}'s '{s.Need}' from map '{s.Map}'",
                new { ok = true, map = s.Map, repo = s.Repo, need = s.Need, removed = true });
        }

        var target = s.Target ?? throw new ArgumentException("give a target capability or provider-repo/capability (or --rm)");
        editor.SetWiring(s.Map, s.Repo, s.Need, target);
        return CliOutput.Ok(s.Json, $"map '{s.Map}' wires {s.Repo}'s '{s.Need}' to {target}",
            new { ok = true, map = s.Map, repo = s.Repo, need = s.Need, target });
    }
}

/// <summary>The human and JSON renderings of a <see cref="MapInspection"/>, shared by <c>map show</c> and
/// <c>map check</c> so the two never disagree.</summary>
static class MapReport
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Json(MapInspection i, bool ok) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok,
        name = i.Map.Name,
        ready = i.IsReady,
        map = i.Map,
        repos = i.Map.Repos.Select(r => r.Name),
        problems = i.Problems,
        needs = i.Needs,
        provides = i.Provides,
        hubs = i.Hubs.Select(h => new { h.Repo, h.Module, h.ReliedOnBy }),
        fixedValues = i.FixedValues,
        fixes = Fixes(i),
    }, JsonOptions));

    public static void Full(IAnsiConsole ansi, MapInspection i)
    {
        Verdict(ansi, i);
        ansi.MarkupLine($"  [dim]repos[/] {Markup.Escape(string.Join(", ", i.Map.Repos.Select(r => r.Name)))}");

        if (i.Needs.Any())
        {
            ansi.WriteLine();
            var table = CliFormat.Table("NEEDED BY", "NEED", "STATUS", "FROM");
            foreach (var n in i.Needs.OrderBy(n => Rank(n.State)).ThenBy(n => n.Repo, StringComparer.Ordinal))
                table.AddRow(Markup.Escape($"{n.Repo} › {n.Module}"), Markup.Escape(n.Capability), Status(n.State), From(n));
            ansi.Write(table);
        }

        Section(ansi, "Relied on by several modules", i.Hubs.Select(h =>
            $"{Markup.Escape($"{h.Repo} › {h.Module}")}  [dim]{h.ReliedOnBy} modules[/]"));
        Section(ansi, "Constants provided by modules", i.Provides.Where(p => p.Constants is not null).Select(p =>
            $"{Markup.Escape($"{p.Repo} › {p.Module}")}  {Markup.Escape(p.Capability)}  [mediumpurple1]{Markup.Escape(Pairs(p.Constants!))}[/]"));
        Section(ansi, "Provided but unused", i.Provides.Where(p => p.Use == ProvideUse.Unused).Select(p =>
            $"{Markup.Escape($"{p.Repo} › {p.Module}")}  {Markup.Escape(p.Capability)}"));
        Section(ansi, "Fixed values (the same in every workspace)", i.FixedValues.Select(f =>
            $"{Markup.Escape($"{f.Repo} › {f.Module}")}  [yellow]{Markup.Escape($"{f.Key} = {f.DisplayValue}")}[/]  [dim]{Markup.Escape(f.File)}[/]"));
        Blockers(ansi, i, listNeeds: false);
    }

    public static void Verdict(IAnsiConsole ansi, MapInspection i)
    {
        var blocked = i.Gaps.Count + i.Ambiguities.Count;
        var verdict = i.IsReady
            ? $"[green]{Glyph.Check(ansi)}[/] every need is met"
            : blocked > 0
                ? $"[red]✗[/] {blocked} {(blocked == 1 ? "need" : "needs")} can't be met, so checkout would stop"
                : $"[red]✗[/] {i.Problems.Count} {(i.Problems.Count == 1 ? "repo" : "repos")} can't be read";
        ansi.MarkupLine($"[bold]{Markup.Escape(i.Map.Name)}[/]  {verdict}");
    }

    /// <summary>What stops a checkout, each with the command that fixes it where there is one.</summary>
    /// <param name="listNeeds">False when the needs table above already shows the blocked needs.</param>
    public static void Blockers(IAnsiConsole ansi, MapInspection i, bool listNeeds = true)
    {
        Section(ansi, "Repos that can't be read", i.Problems.Select(p =>
            $"{Markup.Escape(p.Repo)}  [dim]{Markup.Escape(p.Message)}[/]"));
        if (listNeeds)
            Section(ansi, "Needs that can't be met", i.Gaps.Concat(i.Ambiguities).Select(n =>
                $"{Markup.Escape($"{n.Repo} › {n.Module}")}  {Markup.Escape(n.Capability)}  {Status(n.State)}  {From(n)}"));
        Section(ansi, "To fix", Fixes(i).Select(f => $"[dim]$[/] {Markup.Escape(f)}"));
    }

    /// <summary>One suggested command per unmet or ambiguous need.</summary>
    static IReadOnlyList<string> Fixes(MapInspection i)
    {
        var map = Quote(i.Map.Name);
        var fixes = new List<string>();
        foreach (var n in i.Ambiguities)
            fixes.Add($"sprig map wire {map} {Quote(n.Repo)} {n.Capability} {n.Providers[0].Repo}/{n.Providers[0].Capability}");
        foreach (var n in i.Gaps)
            fixes.Add(n.Similar is { } similar
                ? $"sprig map wire {map} {Quote(n.Repo)} {n.Capability} {similar.Capability}"
                : $"sprig map default {map} {Quote(n.Repo)} {n.Capability} " + (n.Hints.Count > 0
                    ? string.Join(" ", n.Hints.Select(h => Quote($"{h.Output}={h.Value}")))
                    : "url=<value>"));
        return fixes;
    }

    static string Quote(string s) => s.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '&' or '|' or '<' or '>') ? $"\"{s}\"" : s;

    static int Rank(NeedState s) => s switch { NeedState.Gap => 0, NeedState.Ambiguous => 1, NeedState.Default => 2, _ => 3 };

    static string Status(NeedState s) => s switch
    {
        NeedState.Gap => "[red]unmet[/]",
        NeedState.Ambiguous => "[yellow]ambiguous[/]",
        NeedState.Default => "[mediumpurple1]constant[/]",
        _ => "[blue]wired[/]",
    };

    static string From(NeedInfo n) => n.State switch
    {
        NeedState.Wired => Markup.Escape($"{n.Providers[0].Repo} › {n.Providers[0].Module}")
            + (n.Target != n.Capability ? $" [dim]via {Markup.Escape(n.Target)}[/]" : ""),
        NeedState.Default => $"[mediumpurple1]{Markup.Escape(Pairs(n.DefaultValues!))}[/] [dim]set on the map[/]",
        NeedState.Ambiguous => Markup.Escape(string.Join(" and ", n.Providers.Select(p => p.Repo))) + " [dim]both provide it[/]",
        _ => "[dim]nothing provides it[/]"
            + (n.Similar is { } s ? $" [yellow]· similar: {Markup.Escape($"{s.Capability} ({s.Repo})")}[/]" : "")
            + (n.Hints.Count > 0 ? $" [dim]· was {Markup.Escape(Pairs(n.Hints.ToDictionary(h => h.Output, h => h.Value)))}[/]" : ""),
    };

    static string Pairs(IReadOnlyDictionary<string, string> values) => string.Join(", ", values.Select(v => $"{v.Key} = {v.Value}"));

    static void Section(IAnsiConsole ansi, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        ansi.WriteLine();
        ansi.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        foreach (var line in list) ansi.MarkupLine("  " + line);
    }
}
