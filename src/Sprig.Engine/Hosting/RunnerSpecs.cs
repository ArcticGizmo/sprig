using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;

namespace Sprig.Engine.Hosting;

/// <summary>Turns a sprout's apps into what the host runs: the command for the app's kind, its folder in the sprout,
/// its process env vars, and the port that says it's ready.</summary>
public static class RunnerSpecs
{
    /// <param name="apps">Only these apps (as <c>name</c> or <c>repo/name</c>); null for all of them.</param>
    public static IReadOnlyList<RunnerSpec> For(SproutRecord record, Plan plan, IReadOnlyCollection<string>? apps = null)
    {
        var chosen = plan.Apps.Where(a => apps is null || apps.Contains(a.Name) || apps.Contains($"{a.Owner}/{a.Name}")).ToArray();
        if (apps is not null)
        {
            var unknown = apps.Where(n => !plan.Apps.Any(a => a.Name == n || $"{a.Owner}/{a.Name}" == n)).ToArray();
            if (unknown.Length > 0)
                throw new InvalidOperationException($"{record.Trellis} has no app {string.Join(", ", unknown.Select(u => $"'{u}'"))}; " +
                    $"it has {string.Join(", ", plan.Apps.Select(a => $"{a.Owner}/{a.Name}"))}");
        }
        return chosen.Select(app => For(record, plan, app)).ToArray();
    }

    static RunnerSpec For(SproutRecord record, Plan plan, PlannedApp app)
    {
        var repo = record.Repos.Single(r => r.RepoPath == app.RepoPath);
        var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in app.Env.Where(e => e.File is null)) env[e.Key] = e.Value.Value ?? "";
        var port = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name)?.Port;

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

        return new RunnerSpec(record.Key, $"{app.Owner}/{app.Name}", command, dir, env, build, build is null ? null : repo.Dir, port);
    }

    /// <summary>A command line as the user would type it, through the platform's shell (npm and friends are scripts).</summary>
    static CommandSpec Shell(string line)
        => OperatingSystem.IsWindows() ? new CommandSpec("cmd.exe", ["/d", "/s", "/c", line]) : new CommandSpec("/bin/sh", ["-c", line]);
}
