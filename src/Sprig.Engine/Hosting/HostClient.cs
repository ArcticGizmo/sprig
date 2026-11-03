using System.Diagnostics;
using System.IO.Pipes;
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
    /// Stop a sprout's apps (or one of them) and wait until they've gone, so a caller can then remove what they were
    /// using. Returns the final state of each one stopped; empty when nothing was running or no host is.
    /// </summary>
    public IReadOnlyList<RunnerStatus> StopAndWait(string sprout, string? name = null)
    {
        bool Mine(RunnerStatus r) => r.Sprout == sprout && (name is null || r.Name == name);
        var before = TrySend(new HostRequest("list"))?.Runners?.Where(Mine).Where(r => r.State.IsAlive() || r.State == RunnerState.Stopping).ToArray();
        if (before is not { Length: > 0 }) return [];

        TrySend(new HostRequest("stop", Sprout: sprout, Name: name));
        var deadline = Stopwatch.StartNew();
        var last = before.Select(r => r with { State = RunnerState.Stopping }).ToDictionary(r => r.Name);
        while (deadline.Elapsed < Supervisor.Grace + TimeSpan.FromSeconds(15))
        {
            var now = TrySend(new HostRequest("list"))?.Runners?.Where(Mine).ToDictionary(r => r.Name);
            if (now is null)
            {
                // Its apps went with it (their jobs close with the host), but that's not the stop that was asked for.
                foreach (var key in last.Keys.ToArray().Where(n => last[n].State == RunnerState.Stopping))
                    last[key] = last[key] with { State = RunnerState.Stopped, Error = "the sprig host stopped answering; its apps ended with it" };
                break;
            }
            foreach (var r in before.Where(b => now.ContainsKey(b.Name))) last[r.Name] = now[r.Name];
            // A whole-sprout stop forgets each runner once it has stopped, so gone counts as stopped.
            foreach (var r in before.Where(b => !now.ContainsKey(b.Name))) last[r.Name] = last[r.Name] with { State = RunnerState.Stopped };
            if (last.Values.All(r => r.State != RunnerState.Stopping)) break;
            Thread.Sleep(150);
        }
        return last.Values.ToArray();
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

    // Installed, sprig-host sits beside the CLI and the app. In a dev build it's in its own project's bin folder. A
    // referencing project gets a bare exe stub without its dll, so a candidate only counts with its dll beside it.
    static string Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "sprig-host.exe" : "sprig-host";
        foreach (var dir in Candidates())
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "sprig-host.dll"))) return candidate;
        }
        throw new FileNotFoundException("couldn't find sprig-host; build Sprig.Host");
    }

    static IEnumerable<string> Candidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        var repo = new DirectoryInfo(baseDir);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "sprig.slnx"))) repo = repo.Parent;
        if (repo is null) yield break;
        var tfm = new DirectoryInfo(baseDir).Name;
        var config = new DirectoryInfo(baseDir).Parent?.Name ?? "Debug";
        foreach (var c in new[] { config, "Debug", "Release" })
            yield return Path.Combine(repo.FullName, "src", "Sprig.Host", "bin", c, tfm);
    }
}
