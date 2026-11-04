using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sprig.Engine.Hosting;

namespace Sprig.App;

/// <summary>
/// What the sprig host says about every runner, of every sprout, asked once for the whole app so the nav and the
/// Sprouts page show the same thing without each asking the host on its own timer.
/// </summary>
public sealed class HostStatus(HostClient host)
{
    bool _polling;
    IReadOnlyList<RunnerStatus>? _held;
    bool _holding;

    /// <summary>Answer every poll with <paramref name="runners"/> instead of asking the host, so a rendered sample
    /// keeps its made-up states while the window's timer ticks.</summary>
    public void Hold(IReadOnlyList<RunnerStatus>? runners)
    {
        _held = runners;
        _holding = true;
        Apply(runners);
    }

    /// <summary>Null when no host is running, which means nothing is running.</summary>
    public IReadOnlyList<RunnerStatus>? Runners { get; private set; }

    /// <summary>Raised after each answer, on the thread that called <see cref="PollAsync"/>.</summary>
    public event Action<IReadOnlyList<RunnerStatus>?>? Changed;

    /// <summary>Ask the host, off the UI thread. A call while one is still waiting does nothing.</summary>
    public async Task PollAsync()
    {
        if (_holding)
        {
            Apply(_held);
            return;
        }
        if (_polling) return;
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
