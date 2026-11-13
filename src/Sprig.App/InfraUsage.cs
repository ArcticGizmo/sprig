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

    bool _polling;
    bool _holding;
    DateTime _next = DateTime.MinValue;

    /// <summary>By compose project and service; empty until docker has answered.</summary>
    public IReadOnlyDictionary<(string Project, string Service), ResourceUsage> Usage { get; private set; }
        = new Dictionary<(string, string), ResourceUsage>();

    /// <summary>Raised after each answer, on the thread that called <see cref="PollAsync"/>.</summary>
    public event Action? Changed;

    /// <summary>For a rendered sample: these figures stand, and docker isn't asked.</summary>
    public void Hold(IReadOnlyDictionary<(string Project, string Service), ResourceUsage> usage)
    {
        _holding = true;
        Usage = usage;
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
            Changed?.Invoke();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Ask at the next poll rather than waiting out the beat, after infra was started or stopped.</summary>
    public void Soon() => _next = DateTime.MinValue;

    /// <summary>One workspace's containers added up; null when none of them is running or docker hasn't said.</summary>
    public ResourceUsage? Of(WorkspaceRecord record)
    {
        var projects = WorkspaceService.InfraProjects(record).ToHashSet(StringComparer.Ordinal);
        return ResourceUsage.Sum(Usage.Where(u => projects.Contains(u.Key.Project)).Select(u => (ResourceUsage?)u.Value));
    }
}
