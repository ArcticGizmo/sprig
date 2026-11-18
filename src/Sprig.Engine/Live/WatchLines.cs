using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Engine.Live;

/// <summary>
/// The lines <c>sprig watch --json</c> writes: a public contract, so these are their own types rather than the engine's
/// records, which stay free to change. A consumer ignores any <c>type</c> or field it doesn't know, and reads what it
/// does know from a higher <see cref="Schema"/>. A field left out is null.
/// </summary>
public static class WatchLines
{
    public const int Schema = 1;

    /// <summary>What an app with no runner says: the plan has it, the host isn't running it.</summary>
    public const string Off = "off";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object line) => JsonSerializer.Serialize(line, line.GetType(), Json);

    /// <summary>
    /// Every workspace of <paramref name="workspaces"/> as it is now. Its apps are its plan's, in the plan's order, each
    /// with the host's state or <see cref="Off"/>, then any the host runs that the plan no longer has. Docker isn't
    /// asked, so a workspace's state counts its apps only (see <see cref="WatchSnapshot.Infra"/>).
    /// </summary>
    /// <param name="host">The host's answer; null when no host is running.</param>
    public static WatchSnapshot Build(IEnumerable<(WorkspaceRecord Record, Plan? Plan)> workspaces, HostResponse? host, bool usage)
    {
        var runners = host?.Runners ?? [];
        return new WatchSnapshot(
            host is null ? new WatchHost(false) : new WatchHost(true, host.Pid, host.Version),
            workspaces.Select(w => Workspace(w.Record, w.Plan, runners, usage)).ToArray());
    }

    static WatchWorkspace Workspace(WorkspaceRecord record, Plan? plan, IReadOnlyList<RunnerStatus> runners, bool usage)
    {
        var mine = runners.Where(r => r.Workspace == record.Key).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var names = (plan?.Apps.Select(a => a.Key) ?? []).Concat(mine.Keys.Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal);
        var apps = names.Select(n => mine.TryGetValue(n, out var r) ? App(r, usage) : new WatchApp(n, Off)).ToArray();
        var readiness = AppsReadiness.Of(record, plan, runners);
        return new WatchWorkspace(record.Key, record.Orchestration, record.Name, record.Label, record.Branch,
            WorkspaceService.RunState(record, runners, null), record.Paused is not null, record.Error, record.Folder,
            record.Repos.Select(r => r.Dir).ToArray(), record.MadeForAgent,
            new WatchReadiness(JsonNamingPolicy.CamelCase.ConvertName(readiness.Health.ToString()), readiness.Ready, readiness.Started,
                readiness.Total, readiness.Down),
            apps);
    }

    static WatchApp App(RunnerStatus r, bool usage)
        => new(r.Name, JsonNamingPolicy.CamelCase.ConvertName(r.State.ToString()), r.Port, r.Pid, r.Exit, r.Error, r.Restarts, r.Doing, r.Since,
            r.LogFile, usage && r.Usage is { } u ? new WatchUsage(u.Cpu, u.Memory, u.Processes) : null);
}

/// <summary>The first line, and the line after the host comes back or something was missed: everything, so a consumer
/// never has to replay or diff.</summary>
public sealed record WatchSnapshot(WatchHost Host, IReadOnlyList<WatchWorkspace> Workspaces)
{
    [JsonPropertyOrder(-3)] public string Type => "snapshot";
    [JsonPropertyOrder(-2)] public int Schema => WatchLines.Schema;

    /// <summary>Containers aren't counted in a workspace's state: one with only its infra up reads stopped or paused.</summary>
    [JsonPropertyOrder(-1)] public string Infra => "not-counted";
}

/// <summary>In a snapshot, and alone (<c>type: host</c>) when the host has gone. Its return comes as a snapshot.</summary>
public sealed record WatchHost(bool Running, int? Pid = null, string? Version = null)
{
    [JsonPropertyOrder(-1)] public string? Type { get; init; }
}

/// <param name="State">Whether anything of it is going: free, starting (being made ready), paused, running, stopped or
/// failed. Whether what's going is up is <paramref name="Readiness"/>.</param>
/// <param name="Dirs">Where each of its repos is checked out, to match a session's folder to its workspace.</param>
/// <param name="MadeForAgent">The agent session it was made for, when a hook made it.</param>
/// <param name="Apps">In a snapshot; left out of a <c>workspace</c> line, whose apps come as <c>app</c> lines.</param>
public sealed record WatchWorkspace(string Key, string Orchestration, string Name, string Label, string? Branch, string State,
    bool Paused, string? Error, string? Folder, IReadOnlyList<string> Dirs, string? MadeForAgent, WatchReadiness Readiness,
    IReadOnlyList<WatchApp>? Apps)
{
    [JsonPropertyOrder(-1)] public string? Type { get; init; }
}

/// <summary>How far its apps have got (see <see cref="AppsReadiness"/>), so a consumer needn't work it out from them.</summary>
/// <param name="Health"><c>none</c> (no app asked for), <c>starting</c> (one still on its way up), <c>up</c> or
/// <c>trouble</c> (one exited, failed or restarting, which outweighs starting).</param>
/// <param name="Ready">Apps, not jobs, that are ready.</param>
/// <param name="Started">Apps, not jobs, asked for and not stopped.</param>
/// <param name="Total">Every app its plan has, jobs aside.</param>
/// <param name="Down">What's in trouble, by name.</param>
public sealed record WatchReadiness(string Health, int Ready, int Started, int Total, IReadOnlyList<string> Down);

/// <summary>An app as a snapshot lists it, or as an <c>app</c> line sends it: every field, not only what changed.</summary>
/// <param name="Name"><c>repo/app</c>.</param>
/// <param name="State">The runner's state in camelCase (<c>building</c>, <c>ready</c>, <c>exited</c> ...), or
/// <see cref="WatchLines.Off"/>.</param>
/// <param name="Usage">With <c>--usage</c>, while it runs.</param>
public sealed record WatchApp(string Name, string State, int? Port = null, int? Pid = null, int? Exit = null, string? Error = null,
    int? Restarts = null, string? Doing = null, DateTimeOffset? Since = null, string? LogFile = null, WatchUsage? Usage = null)
{
    [JsonPropertyOrder(-2)] public string? Type { get; init; }
    [JsonPropertyOrder(-1)] public string? Workspace { get; init; }
}

/// <param name="Cpu">A share of the whole machine, 0–100; null until a second sample.</param>
/// <param name="Memory">Bytes.</param>
public sealed record WatchUsage(double? Cpu, long Memory, int Processes);

public sealed record WatchWorkspaceRemoved(string Key)
{
    [JsonPropertyOrder(-1)] public string Type => "workspace-removed";
}

/// <summary>Its workspace's plan no longer has it, and the host isn't running it.</summary>
public sealed record WatchAppRemoved(string Workspace, string Name)
{
    [JsonPropertyOrder(-1)] public string Type => "app-removed";
}

/// <summary>Turns one snapshot after another into the lines that say what changed between them.</summary>
public sealed class WatchDiff
{
    WatchSnapshot? _last;

    /// <param name="fresh">Send a whole snapshot whatever changed, as after missing changes.</param>
    public IReadOnlyList<object> Next(WatchSnapshot now, bool fresh = false)
    {
        var last = _last;
        _last = now;
        // A host that's come back (or been replaced) may have run anything meanwhile.
        if (last is null || fresh || now.Host.Running && (!last.Host.Running || now.Host.Pid != last.Host.Pid)) return [now];

        var lines = new List<object>();
        if (last.Host.Running && !now.Host.Running) lines.Add(now.Host with { Type = "host" });
        var keys = now.Workspaces.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        lines.AddRange(last.Workspaces.Where(w => !keys.Contains(w.Key)).Select(w => new WatchWorkspaceRemoved(w.Key)));

        var before = last.Workspaces.ToDictionary(w => w.Key, StringComparer.Ordinal);
        foreach (var workspace in now.Workspaces)
        {
            before.TryGetValue(workspace.Key, out var was);
            var bare = workspace with { Apps = null, Type = "workspace" };
            if (was is null || Same(bare, was with { Apps = null, Type = "workspace" }) is false) lines.Add(bare);

            var apps = was?.Apps?.ToDictionary(a => a.Name, StringComparer.Ordinal) ?? [];
            foreach (var app in workspace.Apps ?? [])
                if (!apps.TryGetValue(app.Name, out var wasApp) || !Same(app, wasApp)) lines.Add(app with { Type = "app", Workspace = workspace.Key });
            var names = (workspace.Apps ?? []).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            lines.AddRange(apps.Keys.Where(n => !names.Contains(n)).Select(n => new WatchAppRemoved(workspace.Key, n)));
        }
        return lines;
    }

    // As written, since records holding lists don't compare by content.
    static bool Same(object a, object b) => WatchLines.Serialize(a) == WatchLines.Serialize(b);
}
