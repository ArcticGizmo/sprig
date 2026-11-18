using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sprig.Engine.Store;

namespace Sprig.Engine.Hosting;

/// <summary>Talks to this store's <c>sprig-host</c>, starting it when it isn't running. The CLI and the app both use
/// it, so they see and control the same runners.</summary>
public sealed class HostClient(ISprigPaths paths)
{
    /// <summary>The host's answer, or null when no host is running for this store (or it was shutting down and
    /// dropped the connection unanswered).</summary>
    public HostResponse? TrySend(HostRequest request, TimeSpan? timeout = null)
    {
        using var pipe = new NamedPipeClientStream(".", HostProtocol.PipeName(paths), PipeDirection.InOut,
            PipeOptions.CurrentUserOnly);
        try { pipe.Connect((int)(timeout ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds); }
        catch (TimeoutException) { return null; }

        string? line;
        try
        {
            // Written straight to the pipe rather than through a StreamWriter: disposing one flushes, and after a
            // shutdown the host has already answered and closed its end, so that flush would throw.
            pipe.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HostProtocol.Json) + "\n"));
            using var reader = new StreamReader(pipe, leaveOpen: true);
            line = reader.ReadLine();
        }
        catch (IOException) { return null; }
        return line is null ? null : JsonSerializer.Deserialize<HostResponse>(line, HostProtocol.Json);
    }

    /// <summary>
    /// Have the host build <paramref name="apps"/> (null for every one) and what they depend on while the workspace's
    /// infra comes up, holding them until a start for the workspace without a hold releases them. That release must not
    /// name them to restart, or they'd be built again. Returns the workspace's key when it did, for stopping them should
    /// bringing it up fail. Only a head start: null when it didn't, and starting them afterwards builds as usual.
    /// </summary>
    /// <param name="jobsAgain">Run their jobs again even if they've finished, as after the workspace's data is emptied.</param>
    public string? Prebuild(Workspaces.WorkspaceRecord record, Planning.Plan plan, IReadOnlyCollection<string>? apps, bool jobsAgain = false)
    {
        try
        {
            var specs = RunnerSpecs.For(record, plan, apps);
            if (specs.Count == 0) return null;
            return Send(new HostRequest("start", specs, Restart: jobsAgain ? RunnerSpecs.Jobs(specs) : [], Hold: true)).Ok ? record.Key : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Stop a workspace's apps (or one of them) and wait until they've gone, so a caller can then remove what they were
    /// using. Returns the final state of each one stopped; empty when nothing was running or no host is.
    /// </summary>
    public IReadOnlyList<RunnerStatus> StopAndWait(string workspace, string? name = null, CancellationToken cancellation = default)
    {
        bool Mine(RunnerStatus r) => r.Workspace == workspace && (name is null || r.Name == name);
        var before = TrySend(new HostRequest("list"))?.Runners?.Where(Mine).Where(r => r.State.IsAlive() || r.State == RunnerState.Stopping).ToArray();
        if (before is not { Length: > 0 }) return [];

        TrySend(new HostRequest("stop", Workspace: workspace, Name: name));
        var last = before.Select(r => r with { State = RunnerState.Stopping }).ToDictionary(r => r.Name);
        // The host carries on stopping them; only the waiting is given up.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Supervisor.Grace + TimeSpan.FromSeconds(15));
        foreach (var runners in Follow(deadline.Token))
        {
            if (runners is null)
            {
                // Its apps went with it (their jobs close with the host), but that's not the stop that was asked for.
                foreach (var key in last.Keys.ToArray().Where(n => last[n].State == RunnerState.Stopping))
                    last[key] = last[key] with { State = RunnerState.Stopped, Error = "the sprig host stopped answering; its apps ended with it" };
                break;
            }
            var now = runners.Where(Mine).ToDictionary(r => r.Name);
            foreach (var r in before.Where(b => now.ContainsKey(b.Name))) last[r.Name] = now[r.Name];
            // A whole-workspace stop forgets each runner once it has stopped, so gone counts as stopped.
            foreach (var r in before.Where(b => !now.ContainsKey(b.Name))) last[r.Name] = last[r.Name] with { State = RunnerState.Stopped };
            if (last.Values.All(r => r.State != RunnerState.Stopping)) break;
        }
        cancellation.ThrowIfCancellationRequested();
        return last.Values.ToArray();
    }

    /// <summary>How long a follower waits before looking for a host again while none answers. A look for a missing pipe
    /// costs next to nothing, but it isn't instant: the connect waits out its timeout.</summary>
    public static readonly TimeSpan LookAgain = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The host's runner list now, then each time it changes; null whenever no host is running, which means nothing is.
    /// Never starts a host, so following one never keeps it alive; while none answers it looks again every
    /// <paramref name="retry"/> (default <see cref="LookAgain"/>). Ends, without throwing, when
    /// <paramref name="cancellation"/> fires.
    /// </summary>
    /// <param name="usage">Include what each runner uses, sent with every sample.</param>
    public async IAsyncEnumerable<IReadOnlyList<RunnerStatus>?> SubscribeAsync(bool usage = false, TimeSpan? retry = null,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await foreach (var answer in SubscribeAnswersAsync(usage, retry, cancellation)) yield return answer?.Runners;
    }

    /// <summary>As <see cref="SubscribeAsync"/>, with the host's whole answer: its pid and version too (null from a host
    /// from before subscribing), and <see cref="HostResponse.Runners"/> never null.</summary>
    public async IAsyncEnumerable<HostResponse?> SubscribeAnswersAsync(bool usage = false, TimeSpan? retry = null,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        bool? hostWas = null;
        while (!cancellation.IsCancellationRequested)
        {
            await using (var pipe = await ConnectAsync(cancellation))
            {
                if (pipe is not null && await WriteLineAsync(pipe, new HostRequest("subscribe", Usage: usage), cancellation))
                {
                    var reader = new StreamReader(pipe, leaveOpen: true);
                    while (await ReadAnswerAsync(reader, cancellation) is { } answer)
                    {
                        if (!answer.Ok)
                        {
                            // A host from before subscribing could be asked (still running after an update); it can only
                            // be polled, until it ends.
                            await foreach (var runners in PollAsync(usage, retry ?? LookAgain, cancellation))
                            {
                                hostWas = true;
                                yield return new HostResponse(true, Runners: runners);
                            }
                            break;
                        }
                        hostWas = true;
                        yield return answer with { Runners = answer.Runners ?? [] };
                    }
                }
            }
            if (cancellation.IsCancellationRequested) yield break;
            if (hostWas != false)
            {
                hostWas = false;
                yield return null;
            }
            await DelayAsync(retry ?? LookAgain, cancellation);
        }
    }

    /// <summary><see cref="SubscribeAsync"/> for a caller that waits on a thread of its own, as the CLI does.</summary>
    public IEnumerable<IReadOnlyList<RunnerStatus>?> Follow(CancellationToken cancellation, bool usage = false)
        => SubscribeAsync(usage, cancellation: cancellation).ToBlockingEnumerable(cancellation);

    /// <summary>Each changed answer to <c>list</c> while the host gives one.</summary>
    async IAsyncEnumerable<IReadOnlyList<RunnerStatus>> PollAsync(bool usage, TimeSpan every, [EnumeratorCancellation] CancellationToken cancellation)
    {
        IReadOnlyList<RunnerStatus>? sent = null;
        while (!cancellation.IsCancellationRequested && TrySend(new HostRequest("list"))?.Runners is { } runners)
        {
            if (!usage) runners = runners.Select(r => r with { Usage = null }).ToArray();
            if (sent is null || usage || !runners.SequenceEqual(sent)) yield return sent = runners;
            await DelayAsync(every, cancellation);
        }
    }

    async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancellation)
    {
        var pipe = new NamedPipeClientStream(".", HostProtocol.PipeName(paths), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(500, cancellation);
            return pipe;
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    static async Task<bool> WriteLineAsync(Stream pipe, HostRequest request, CancellationToken cancellation)
    {
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HostProtocol.Json) + "\n"), cancellation);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Null once the host has gone, or sent something that isn't an answer.</summary>
    static async Task<HostResponse?> ReadAnswerAsync(StreamReader reader, CancellationToken cancellation)
    {
        try
        {
            return await reader.ReadLineAsync(cancellation) is { } line ? JsonSerializer.Deserialize<HostResponse>(line, HostProtocol.Json) : null;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or ObjectDisposedException)
        {
            return null;
        }
    }

    static async Task DelayAsync(TimeSpan wait, CancellationToken cancellation)
    {
        try { await Task.Delay(wait, cancellation); }
        catch (OperationCanceledException) { }
    }

    static bool Alive(RunnerStatus r) => r.State.IsAlive() || r.State == RunnerState.Stopping;

    /// <summary>Stop every app of every workspace, waiting for each to go, then end the host itself.</summary>
    public IReadOnlyList<RunnerStatus> StopAllAndShutdown()
    {
        var workspaces = TrySend(new HostRequest("list"))?.Runners?.Where(Alive).Select(r => r.Workspace).Distinct().ToArray() ?? [];
        var stopped = workspaces.AsParallel().SelectMany(s => StopAndWait(s)).ToArray();
        TrySend(new HostRequest("shutdown"));
        return stopped;
    }

    /// <summary>
    /// End the host when nothing is running in it, rather than leaving it to sit out its idle time holding the store's
    /// files open. False (and the host left alone) when an app is still going.
    /// </summary>
    public bool ShutdownIfIdle()
    {
        if (TrySend(new HostRequest("list"))?.Runners is not { } runners) return true;
        if (runners.Any(Alive)) return false;
        TrySend(new HostRequest("shutdown"));
        return true;
    }

    /// <summary>Send <paramref name="request"/>, starting the host first when it isn't running.</summary>
    public HostResponse Send(HostRequest request)
    {
        if (TrySend(request) is { } answer) return answer;
        Launch();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (TrySend(request, TimeSpan.FromMilliseconds(250)) is { } response) return response;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the sprig host didn't start; run sprig-host directly to see why");
    }

    /// <summary>
    /// Shell-executed so the host inherits none of the caller's handles. Started the ordinary way it would keep the CLI's
    /// stdout open, and anything reading that output (<c>sprig start | tail</c>, a script, a CI step) would wait for the
    /// host to exit.
    /// </summary>
    void Launch()
    {
        Directory.CreateDirectory(paths.Root);
        var psi = new ProcessStartInfo(Locate())
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = paths.Root,
            Arguments = $"--store \"{Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar)}\"",
        };
        Process.Start(psi)?.Dispose();
    }

    // Installed, sprig-host is a single file beside the CLI and the app. In a dev build it's in its own project's bin
    // folder, and a referencing project's bin gets a bare exe stub without its dll, so there a candidate only counts
    // with its dll beside it.
    static string Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "sprig-host.exe" : "sprig-host";
        var baseDir = AppContext.BaseDirectory;
        var repo = RepoAbove(baseDir);
        if (repo is null)
        {
            var installed = Path.Combine(baseDir, exe);
            if (File.Exists(installed)) return installed;
            throw new FileNotFoundException($"couldn't find sprig-host beside sprig in {baseDir}; reinstall sprig");
        }
        foreach (var dir in DevCandidates(baseDir, repo))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "sprig-host.dll"))) return candidate;
        }
        throw new FileNotFoundException("couldn't find sprig-host; build Sprig.Host");
    }

    static DirectoryInfo? RepoAbove(string baseDir)
    {
        var repo = new DirectoryInfo(baseDir);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "sprig.slnx"))) repo = repo.Parent;
        return repo;
    }

    static IEnumerable<string> DevCandidates(string baseDir, DirectoryInfo repo)
    {
        yield return baseDir;
        var tfm = new DirectoryInfo(baseDir).Name;
        var config = new DirectoryInfo(baseDir).Parent?.Name ?? "Debug";
        foreach (var c in new[] { config, "Debug", "Release" })
            yield return Path.Combine(repo.FullName, "src", "Sprig.Host", "bin", c, tfm);
    }
}
