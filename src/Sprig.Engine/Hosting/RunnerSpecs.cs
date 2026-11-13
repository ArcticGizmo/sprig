using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Engine.Hosting;

/// <summary>Turns a workspace's apps and jobs into what the host runs: the command for each kind, its folder in the
/// workspace, the port that says it's ready, and what it waits for.</summary>
public static class RunnerSpecs
{
    /// <summary>
    /// The runners for <paramref name="names"/> (as <c>name</c> or <c>owner/name</c>; null for every app and job) plus
    /// everything they depend on, dependencies first.
    /// </summary>
    public static IReadOnlyList<RunnerSpec> For(WorkspaceRecord record, Plan plan, IReadOnlyCollection<string>? names = null)
        => Closure(plan, Resolve(record, plan, names)).Select(app => For(record, plan, app)).ToArray();

    /// <summary>
    /// What restarting the workspace's running apps takes: every app that's going (of <paramref name="within"/>, when
    /// given), each built again first, plus what they depend on. Those dependencies are left alone, except that
    /// <paramref name="jobs"/> runs their jobs (migrations, say) again before the apps come back. Stopped apps stay
    /// stopped and aren't waited for: one stopped on purpose may be running in an IDE on its port, and starting sprig's
    /// copy beside it would clash. Empty when nothing is running.
    /// </summary>
    /// <param name="runners">What the host says is running.</param>
    /// <param name="within">Only these apps, as <c>name</c> or <c>owner/name</c>.</param>
    public static (IReadOnlyList<RunnerSpec> Specs, IReadOnlyList<string> Restart) RestartRunning(WorkspaceRecord record, Plan plan,
        IReadOnlyList<RunnerStatus> runners, IReadOnlyCollection<string>? within = null, bool jobs = false)
    {
        var allowed = within is null ? null : Resolve(record, plan, within).ToHashSet(StringComparer.Ordinal);
        var apps = plan.Apps.Where(a => !a.Job).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var running = runners
            .Where(r => r.Workspace == record.Key && r.State.IsAlive() && apps.Contains(r.Name) && (allowed is null || allowed.Contains(r.Name)))
            .Select(r => r.Name)
            .ToArray();
        if (running.Length == 0) return ([], []);
        var stopped = runners.Where(r => r.Workspace == record.Key && r.State == RunnerState.Stopped).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var specs = For(record, plan, running)
            .Where(s => !stopped.Contains(s.Name))
            .Select(s => s.WaitFor is { } waits && waits.Any(stopped.Contains) ? s with { WaitFor = waits.Where(w => !stopped.Contains(w)).ToArray() } : s)
            .ToArray();
        return (specs, jobs ? [.. running, .. specs.Where(s => s.Job).Select(s => s.Name)] : running);
    }

    /// <summary>The jobs among <paramref name="specs"/>, by name.</summary>
    public static IReadOnlyList<string> Jobs(IReadOnlyList<RunnerSpec> specs) => specs.Where(s => s.Job).Select(s => s.Name).ToArray();

    /// <summary>The apps of <paramref name="plan"/>'s group <paramref name="name"/>, for <see cref="For"/>, which adds what
    /// they depend on.</summary>
    public static IReadOnlyList<string> Group(Plan plan, string name)
        => plan.Groups.FirstOrDefault(g => g.Name == name)?.Apps
            ?? throw new InvalidOperationException(plan.Groups.Count == 0
                ? $"there's no group '{name}'; the definitions name none"
                : $"there's no group '{name}'; there's {string.Join(", ", plan.Groups.Select(g => g.Name))}");

    /// <summary>The apps <paramref name="names"/> pick out, by their <see cref="PlannedApp.Key"/>.</summary>
    public static IReadOnlyList<string> Resolve(WorkspaceRecord record, Plan plan, IReadOnlyCollection<string>? names)
    {
        if (names is null) return plan.Apps.Select(a => a.Key).ToArray();
        var keys = new List<string>();
        foreach (var name in names)
        {
            var matches = plan.Apps.Where(a => a.Key == name || a.Name == name).ToArray();
            keys.Add(matches.Length switch
            {
                1 => matches[0].Key,
                0 => throw new InvalidOperationException($"{record.Orchestration} has no app '{name}'; it has {string.Join(", ", plan.Apps.Select(a => a.Key))}"),
                _ => throw new InvalidOperationException($"'{name}' is in more than one repo; name it as <repo>/{name}"),
            });
        }
        return keys;
    }

    internal static IEnumerable<PlannedApp> Closure(Plan plan, IReadOnlyList<string> keys)
    {
        var byKey = plan.Apps.ToDictionary(a => a.Key, StringComparer.Ordinal);
        var ordered = new List<PlannedApp>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys) Visit(key);
        return ordered;

        void Visit(string key)
        {
            if (!seen.Add(key) || !byKey.TryGetValue(key, out var app)) return;
            foreach (var dependency in app.DependsOn ?? []) Visit(dependency);
            ordered.Add(app);
        }
    }

    static RunnerSpec For(WorkspaceRecord record, Plan plan, PlannedApp app)
    {
        var repo = record.Repos.Single(r => r.RepoPath == app.RepoPath);
        var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
        var port = app.Job ? null : plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name)?.Port;

        CommandSpec? build = null;
        var buildKey = repo.Dir;
        var node = app.Kind == "node" ? NodePackages.Of(dir, repo.Dir) : null;
        CommandSpec command;
        if (app.Run?.Value is { } run)
            command = Shell(run);
        else if (app.Kind == "dotnet")
        {
            // Built once up front, one build per repo at a time, because sibling apps share projects' obj folders. Then
            // run as a plain dotnet run would, launch profile and all.
            build = new CommandSpec("dotnet", ["build", "--nologo", "-v", "minimal"]);
            command = new CommandSpec("dotnet", ["run", "--no-build"]);
        }
        else if (node is not null)
            command = Shell(node.Pnpm ? "pnpm run dev" : "npm run dev");
        else
            throw new InvalidOperationException($"'{app.Name}' has no Run command, so sprig doesn't know how to start it");

        // A fresh worktree has no node_modules (git ignores it), and a slot taking another branch may have a different
        // lockfile. Install scripts stay off, since they're where malicious packages run their code, unless a pnpm
        // workspace lists the packages it lets run them; pnpm then holds every other package to that list. A pnpm
        // workspace's apps share one install, so it's keyed by the workspace's folder: the second app waits, then finds
        // nothing to do.
        if (node is not null && File.Exists(Path.Combine(dir, "package.json")) && !node.Installed())
        {
            var scripts = node.Pnpm && node.ListsBuilds() ? "" : " --ignore-scripts";
            build = Shell(node.Pnpm
                ? node.Lockfile is null ? $"pnpm install{scripts}" : $"pnpm install --frozen-lockfile{scripts}"
                : node.Lockfile is null ? $"npm install{scripts}" : $"npm ci{scripts}");
            buildKey = node.Root;
        }

        // Main can't always know an app's port, since the repo's own config gives it; it goes without the check then.
        var health = (app.Health ?? [])
            .Select(h => plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name && p.Endpoint == h.Endpoint)?.Port is { } p
                ? $"http://localhost:{p}{h.Path}"
                : record.IsMain ? null
                : throw new InvalidOperationException($"'{app.Key}' checks {h.Path} on {h.Endpoint}, which has no port"))
            .OfType<string>()
            .ToArray();
        var restart = app.Restart is { } r ? new RestartSpec(r.Policy == "always", r.Max) : null;

        return new RunnerSpec(record.Key, app.Key, command, dir, build, build is null ? null : buildKey, port,
            app.DependsOn ?? [], app.Job, health.Length > 0 ? health : null, app.ReadyLog, restart,
            BuildDoing: node is not null && build is not null ? "installing packages" : null);
    }

    /// <summary>A command line as the user would type it, through the platform's shell (npm and friends are scripts).</summary>
    static CommandSpec Shell(string line)
        => OperatingSystem.IsWindows() ? new CommandSpec("cmd.exe", ["/d", "/s", "/c", line]) : new CommandSpec("/bin/sh", ["-c", line]);
}
