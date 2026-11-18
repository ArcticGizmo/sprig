using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sprig.Engine.Hosting;

namespace Sprig.App;

/// <summary>
/// What the sprig host says about every runner, of every workspace, heard once for the whole app so the nav and the
/// Workspaces page show the same thing without each asking the host on its own.
/// </summary>
public sealed class HostStatus(HostClient host)
{
    /// <summary>How soon a host that has just started is noticed, as when the first app of a session starts.</summary>
    static readonly TimeSpan LookAgain = TimeSpan.FromSeconds(1);

    bool _polling;
    IReadOnlyList<RunnerStatus>? _held;
    bool _holding;
    CancellationTokenSource? _following;

    /// <summary>Answer every poll with <paramref name="runners"/> instead of asking the host, so a rendered sample
    /// keeps its made-up states.</summary>
    public void Hold(IReadOnlyList<RunnerStatus>? runners)
    {
        _held = runners;
        _holding = true;
        Apply(runners);
    }

    /// <summary>Null when no host is running, which means nothing is running.</summary>
    public IReadOnlyList<RunnerStatus>? Runners { get; private set; }

    /// <summary>Raised after each answer, on the thread that applied it: the UI thread while following.</summary>
    public event Action<IReadOnlyList<RunnerStatus>?>? Changed;

    /// <summary>
    /// Hear each change from the host as it happens, usage included, until <see cref="Unfollow"/>. Each is applied
    /// through <paramref name="post"/>, which puts it on the UI thread. Following never starts the host, nor keeps one
    /// alive.
    /// </summary>
    public void Follow(Action<Action> post)
    {
        if (_following is not null) return;
        var following = _following = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await foreach (var runners in host.SubscribeAsync(usage: true, retry: LookAgain, cancellation: following.Token))
                post(() =>
                {
                    if (!_holding && !following.IsCancellationRequested) Apply(runners);
                });
        });
    }

    public void Unfollow()
    {
        _following?.Cancel();
        _following = null;
    }

    /// <summary>
    /// Ask the host once, off the UI thread, when nothing is following it; a call while one is still waiting does
    /// nothing. While following it does nothing at all: an answer asked for before a change could land after the change
    /// was heard, and would then stand until the next one.
    /// </summary>
    public async Task PollAsync()
    {
        if (_holding)
        {
            Apply(_held);
            return;
        }
        if (_polling || _following is not null) return;
        _polling = true;
        try
        {
            var answer = await AppServices.RunAsync(() => host.TrySend(new HostRequest("list")));
            Apply(answer?.Runners);
        }
        finally
        {
            _polling = false;
        }
    }

    public void Apply(IReadOnlyList<RunnerStatus>? runners)
    {
        Runners = runners;
        Changed?.Invoke(runners);
    }
}
