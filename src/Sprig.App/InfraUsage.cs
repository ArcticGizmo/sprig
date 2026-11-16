using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.App;

/// <summary>
/// What every up workspace's containers use, asked of docker for them all at once. Docker takes a second or two to
/// measure, so this is asked on its own slower beat, and only while something shows it.
/// </summary>
public sealed class InfraUsage(WorkspaceService workspaces)
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    /// <summary>Docker not answering is likely to last (Docker Desktop isn't running), so it's asked again only now and then.</summary>
    static readonly TimeSpan AfterFailing = TimeSpan.FromSeconds(30);

    /// <summary>Asking which projects run is one <c>docker ps</c>, cheap enough for pages that only show a state.</summary>
    static readonly TimeSpan RunningEvery = TimeSpan.FromSeconds(3);

    bool _polling;
    bool _pollingRunning;
    bool _holding;
    DateTime _next = DateTime.MinValue;
    DateTime _nextRunning = DateTime.MinValue;

    /// <summary>By compose project and service; empty until docker has answered.</summary>
    public IReadOnlyDictionary<(string Project, string Service), ResourceUsage> Usage { get; private set; }
        = new Dictionary<(string, string), ResourceUsage>();

    /// <summary>The compose projects with a container running, from whichever of docker's answers came last: a measure
    /// (<see cref="PollAsync"/>) or a look (<see cref="PollRunningAsync"/>). What every page's states are read from.</summary>
    public IReadOnlySet<string> Running { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The window is open and not minimised; hidden in the tray, a page that only shows states doesn't ask
    /// docker, since nobody is looking.</summary>
    public bool WindowShown { get; set; } = true;

    /// <summary>Ask docker which projects have a container running, when it's been long enough, off the UI thread. For
    /// pages that show workspaces' states but not what they use, so they don't wait on a page that measures.</summary>
    public async Task PollRunningAsync()
    {
        if (_holding || _pollingRunning || !WindowShown || DateTime.UtcNow < _nextRunning) return;
        _pollingRunning = true;
        try
        {
            var answer = await AppServices.RunAsync(workspaces.RunningProjects);
            _nextRunning = DateTime.UtcNow + (answer is null ? AfterFailing : RunningEvery);
            if (answer is null || answer.SetEquals(Running)) return;
            Running = answer;
            Changed?.Invoke();
        }
        finally
        {
            _pollingRunning = false;
        }
    }

    /// <summary>Raised after each answer, on the thread that called <see cref="PollAsync"/>.</summary>
    public event Action? Changed;

    /// <summary>For a rendered sample: these figures stand, and docker isn't asked.</summary>
    public void Hold(IReadOnlyDictionary<(string Project, string Service), ResourceUsage> usage)
    {
        _holding = true;
        Usage = usage;
        Running = usage.Keys.Select(k => k.Project).ToHashSet(StringComparer.Ordinal);
        Changed?.Invoke();
    }

    /// <summary>Ask docker about <paramref name="records"/>' containers when it's been long enough, off the UI thread. A
    /// call while one is waiting does nothing; with nothing up, there's nothing to ask and the figures empty.</summary>
    public async Task PollAsync(IEnumerable<WorkspaceRecord> records)
    {
        if (_holding || _polling || DateTime.UtcNow < _next) return;
        var projects = records.Where(r => r.State == WorkspaceState.Up).SelectMany(WorkspaceService.InfraProjects).ToHashSet(StringComparer.Ordinal);
        if (projects.Count == 0)
        {
            if (Usage.Count == 0) return;
            Usage = new Dictionary<(string, string), ResourceUsage>();
            Changed?.Invoke();
            return;
        }
        _polling = true;
        try
        {
            var answer = await AppServices.RunAsync(() => workspaces.InfraUsage(projects));
            _next = DateTime.UtcNow + (answer is null ? AfterFailing : Every);
            Usage = answer ?? new Dictionary<(string, string), ResourceUsage>();
            // A measure says only about the projects it asked after; what it didn't ask about stays as last seen.
            if (answer is not null)
                Running = Running.Where(p => !projects.Contains(p)).Concat(answer.Keys.Select(k => k.Project)).ToHashSet(StringComparer.Ordinal);
            Changed?.Invoke();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Ask at the next poll rather than waiting out the beat, after infra was started or stopped.</summary>
    public void Soon() => _next = _nextRunning = DateTime.MinValue;

    /// <summary>One workspace's containers added up; null when none of them is running or docker hasn't said.</summary>
    public ResourceUsage? Of(WorkspaceRecord record)
    {
        var projects = WorkspaceService.InfraProjects(record).ToHashSet(StringComparer.Ordinal);
        return ResourceUsage.Sum(Usage.Where(u => projects.Contains(u.Key.Project)).Select(u => (ResourceUsage?)u.Value));
    }
}
