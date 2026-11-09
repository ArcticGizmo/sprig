using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprig.Engine.Planning;

namespace Sprig.App.ViewModels;

public sealed record PortRow(string Owner, string Resource, string Endpoint, string Port, string Pin, bool Failed);

public sealed record SettingRow(string Key, string? File, string Value, string Via, bool Unresolved)
{
    public bool HasFile => File is not null;
}

public sealed record AppSection(string Title, string Subtitle, IReadOnlyList<SettingRow> Rows);

public sealed record ContainerRow(string Owner, string Name, string Image, string Ports, string Volumes);

/// <summary>One value crossing from one repo (or the orchestration) to an app in another.</summary>
public sealed record WiringRow(string Consumer, string Key, string Provider, string Value);

public sealed record GroupRow(string Name, string Apps);

public sealed record ProblemRow(string Severity, string Where, string Message)
{
    public bool IsError => Severity == "error";
}

/// <summary>A <see cref="Plan"/> flattened into rows the Orchestrations page can show. Values are masked; the plan keeps
/// the real ones.</summary>
public sealed class PlanView
{
    public PlanView(Plan plan, string root)
    {
        Summary = $"{Count(plan.Apps.Count, "app")}, {Count(plan.Ports.Count, "port")}.";

        Ports = plan.Ports.Select(p => new PortRow(p.Owner, p.Resource, p.Endpoint, p.Port?.ToString() ?? "none", p.Pin ?? "", p.Port is null)).ToArray();

        Apps = plan.Apps.Select(a => new AppSection(
            $"{a.Owner} / {a.Name}",
            $"{a.Kind} · {a.Path}" + (a.WaitFor.Count > 0 ? $" · waits for {string.Join(", ", a.WaitFor)}" : ""),
            a.Env.Select(e => Row(e.Key, e.File is { } file ? a.FileLabel(file) : null, e.Value))
                .Concat(a.Run is { } run ? [Row("run", null, run)] : [])
                .ToArray())).ToArray();

        Containers = plan.Containers.Select(c => new ContainerRow(c.Owner, c.Name, c.Image, string.Join(", ", c.Ports), string.Join(", ", c.Volumes))).ToArray();

        Compose = plan.Compose.Select(c => new AppSection($"{c.Owner} / {c.File}", "changes sprig makes to the repo's compose file",
            c.Overrides.Select(o => Row(o.Path, null, o.Value)).ToArray())).ToArray();

        Groups = plan.Groups.Select(g => new GroupRow(g.Name, string.Join(", ", g.Apps))).ToArray();

        var values = plan.Apps.SelectMany(a => a.Env.Select(e => (key: (a.Owner, a.Name, e.Key), e.Value.Value)))
            .GroupBy(x => x.key).ToDictionary(g => g.Key, g => g.First().Value);
        Wiring = plan.Wires()
            .Where(w => w.Setting is not null)
            .Select(w => new WiringRow($"{w.ConsumerOwner} / {w.Consumer}", w.Key, Provider(w),
                values.GetValueOrDefault((w.ConsumerOwner, w.Consumer, w.Key)) is { } v ? ValueMask.Apply(v) : "unresolved"))
            .ToArray();

        Problems = Rows(plan.Problems, root);
    }

    public string Summary { get; }
    public IReadOnlyList<PortRow> Ports { get; }
    public IReadOnlyList<AppSection> Apps { get; }
    public IReadOnlyList<ContainerRow> Containers { get; }
    public IReadOnlyList<AppSection> Compose { get; }
    public IReadOnlyList<GroupRow> Groups { get; }
    public IReadOnlyList<WiringRow> Wiring { get; }
    public IReadOnlyList<ProblemRow> Problems { get; }

    public bool HasProblems => Problems.Count > 0;
    public bool HasContainers => Containers.Count > 0;
    public bool HasCompose => Compose.Count > 0;
    public bool HasGroups => Groups.Count > 0;
    public bool HasWiring => Wiring.Count > 0;

    public static IReadOnlyList<ProblemRow> Rows(IEnumerable<PlanProblem> problems, string root) => problems
        .Select(p => new ProblemRow(
            p.Severity == ProblemSeverity.Error ? "error" : "warning",
            p.Location is { } l ? Relative(l.File, root) + (l.Line > 0 ? $":{l.Line}" : "") : "",
            p.Message))
        .ToArray();

    static SettingRow Row(string key, string? file, PlannedValue value)
        => new(key, file, value.Value is { } v ? ValueMask.Apply(v) : Asked(value) is { } asked ? $"asked for: {asked}" : "unresolved",
            string.Join(" → ", value.Via), value.Value is null);

    static string? Asked(PlannedValue value)
        => (value.Sources ?? []).Where(s => s.Owner is null && s.Setting is not null).Select(s => s.Setting!).ToArray() is { Length: > 0 } names
            ? string.Join(", ", names) : null;

    /// <summary>"shop-services / catalog-api (catalog-url)", or "asked for: catalog-url".</summary>
    static string Provider(PlannedWire wire) => wire switch
    {
        { Filled: false } => $"asked for: {wire.Setting}",
        { Provider: { } resource } => $"{wire.ProviderOwner} / {resource} ({wire.Setting})",
        _ => $"{wire.ProviderOwner} ({wire.Setting})",
    };

    static string Relative(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative.Replace('\\', '/');
    }

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}
