using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Engine.Hosting;

/// <summary>Turns a workspace's apps and jobs into what the host runs: the command for each kind, its folder in the
/// workspace, its process env vars, the port that says it's ready, and what it waits for.</summary>
public static class RunnerSpecs
{
    /// <summary>
    /// The runners for <paramref name="names"/> (as <c>name</c> or <c>owner/name</c>; null for every app and job) plus
    /// everything they depend on, dependencies first.
    /// </summary>
    public static IReadOnlyList<RunnerSpec> For(WorkspaceRecord record, Plan plan, IReadOnlyCollection<string>? names = null)
        => Closure(plan, Resolve(record, plan, names)).Select(app => For(record, plan, app)).ToArray();

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

    static IEnumerable<PlannedApp> Closure(Plan plan, IReadOnlyList<string> keys)
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
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in app.Env.Where(e => e.File is null)) env[e.Key] = e.Value.Value ?? "";
        var port = app.Job ? null : plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name)?.Port;

        CommandSpec? build = null;
        CommandSpec command;
        if (app.Run?.Value is { } run)
            command = Shell(run);
        else if (app.Kind == "dotnet")
        {
            // Built once up front, one build per repo at a time, because sibling apps share projects' obj folders. No
            // launch profile: the app's own profile would override the values sprig sets.
            build = new CommandSpec("dotnet", ["build", "--nologo", "-v", "minimal"]);
            command = new CommandSpec("dotnet", ["run", "--no-build", "--no-launch-profile"]);
            // A launch profile is usually where Development comes from; without one the app would run as Production.
            env.TryAdd("ASPNETCORE_ENVIRONMENT", "Development");
            env.TryAdd("DOTNET_ENVIRONMENT", "Development");
        }
        else if (app.Kind == "node")
            command = Shell("npm run dev");
        else
            throw new InvalidOperationException($"'{app.Name}' has no Run command, so sprig doesn't know how to start it");

        // A fresh worktree has no node_modules (git ignores it). Install scripts stay off: they're where malicious
        // packages run their code.
        if (app.Kind == "node" && !Directory.Exists(Path.Combine(dir, "node_modules")) && File.Exists(Path.Combine(dir, "package.json")))
            build = Shell(File.Exists(Path.Combine(dir, "package-lock.json")) ? "npm ci --ignore-scripts" : "npm install --ignore-scripts");

        var health = (app.Health ?? [])
            .Select(h => plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name && p.Endpoint == h.Endpoint)?.Port is { } p
                ? $"http://localhost:{p}{h.Path}"
                : throw new InvalidOperationException($"'{app.Key}' checks {h.Path} on {h.Endpoint}, which has no port"))
            .ToArray();
        var restart = app.Restart is { } r ? new RestartSpec(r.Policy == "always", r.Max) : null;

        return new RunnerSpec(record.Key, app.Key, command, dir, env, build, build is null ? null : repo.Dir, port,
            app.DependsOn ?? [], app.Job, health.Length > 0 ? health : null, app.ReadyLog, restart);
    }

    /// <summary>A command line as the user would type it, through the platform's shell (npm and friends are scripts).</summary>
    static CommandSpec Shell(string line)
        => OperatingSystem.IsWindows() ? new CommandSpec("cmd.exe", ["/d", "/s", "/c", line]) : new CommandSpec("/bin/sh", ["-c", line]);
}
