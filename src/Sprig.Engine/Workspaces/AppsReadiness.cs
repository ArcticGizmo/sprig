using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;

namespace Sprig.Engine.Workspaces;

/// <summary><see cref="AppsReadiness.Starting"/>: some app is on its way up. <see cref="AppsReadiness.Trouble"/>: some
/// app is down that should be up, which outweighs one still starting. <see cref="AppsReadiness.None"/>: none was asked
/// for, or each was stopped.</summary>
public enum AppsHealth { None, Starting, Up, Trouble }

/// <summary>
/// How far a workspace's apps have got, beside <see cref="WorkspaceService.RunState"/>, which says only whether anything
/// of it is going. Running says the workspace is live; this says whether what was asked for is actually up, so a
/// workspace whose builds are still going, or whose api has crashed, isn't shown as fine. Apps stopped on purpose count
/// for nothing.
/// </summary>
/// <param name="Ready">Apps (not jobs) that are ready.</param>
/// <param name="Started">Apps (not jobs) asked for and not stopped, whatever state they're in.</param>
/// <param name="Total">Every app its plan has, jobs aside.</param>
/// <param name="Down">Apps and jobs that exited, failed or are restarting, by name.</param>
public sealed record AppsReadiness(AppsHealth Health, int Ready, int Started, int Total, IReadOnlyList<string> Down)
{
    public static readonly AppsReadiness Nothing = new(AppsHealth.None, 0, 0, 0, []);

    public static AppsReadiness Of(WorkspaceRecord record, Plan? plan, IEnumerable<RunnerStatus> runners)
    {
        var jobs = plan?.Apps.Where(a => a.Job).Select(a => a.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        var mine = runners.Where(r => r.Workspace == record.Key && r.State is not (RunnerState.Stopped or RunnerState.Stopping)).ToArray();
        // Only a job runs to completion, so one in either state is a job even when the plan no longer says so.
        var apps = mine.Where(r => !jobs.Contains(r.Name) && r.State is not (RunnerState.Running or RunnerState.Completed)).ToArray();
        var down = mine.Where(r => r.State.IsDown()).Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();
        var health = down.Length > 0 ? AppsHealth.Trouble
            : mine.Any(r => r.State.IsComingUp()) ? AppsHealth.Starting
            : mine.Length > 0 ? AppsHealth.Up
            : AppsHealth.None;
        var total = Math.Max(plan?.Apps.Count(a => !a.Job) ?? 0, apps.Length);
        return new AppsReadiness(health, apps.Count(r => r.State == RunnerState.Ready), apps.Length, total, down);
    }

    /// <summary>
    /// The word to show for a workspace in <paramref name="runState"/>: <c>starting</c> while it's running and an app is
    /// still on its way up, else the state as it is. Trouble keeps the word (it is running) and is shown by colour and
    /// <see cref="Detail"/>.
    /// </summary>
    public string Word(string runState) => runState == "running" && Health == AppsHealth.Starting ? "starting" : runState;

    /// <summary><see cref="Word"/>, with <see cref="Detail"/> after it when there's any.</summary>
    public string Shown(string runState) => Detail(runState) is { Length: > 0 } detail ? $"{Word(runState)} · {detail}" : Word(runState);

    /// <summary>Shown as up (green): running, with every app it was asked for up.</summary>
    public bool Up(string runState) => runState == "running" && Health is AppsHealth.Up or AppsHealth.None;

    /// <summary>Shown as busy: running, with an app still on its way up.</summary>
    public bool ComingUp(string runState) => runState == "running" && Health == AppsHealth.Starting;

    /// <summary>Shown as bad (red): failed, or running with an app down.</summary>
    public bool InTrouble(string runState) => runState == "failed" || runState == "running" && Health == AppsHealth.Trouble;

    /// <summary>A few words on its apps, for beside the state while it's running; empty when there's nothing to add.</summary>
    public string Detail(string runState) => runState != "running" ? "" : Health switch
    {
        AppsHealth.Starting => Started == 0 ? "running its jobs" : $"{Ready} of {Started} ready",
        AppsHealth.Trouble => Down.Count == 1 ? $"{Down[0]} is down" : $"{Down.Count} apps down",
        _ => "",
    };
}
