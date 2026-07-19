using System.Text.Json;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Cli;

/// <summary>The dev-harness command dispatcher over Sprig.Core (M2 surface: create/ls/info/rm/reconcile).</summary>
public static class CliApp
{
    public static int Run(string[] args)
    {
        var json = Args.TakeFlag(ref args, "--json");
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Help();
        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine(Version());
            return 0;
        }

        var paths = new SprigPaths();
        var git = new GitService(new ProcessRunner());
        var ports = new FilePortStore(paths);
        var instances = new InstanceStore(paths);
        var svc = new WorkspaceService(git, ports, instances, new EnvClobberService());
        var reconciler = new WorkspaceReconciler(git, instances);

        var command = args[0];
        var rest = args[1..];
        try
        {
            return command switch
            {
                "create" => Create(svc, rest, json),
                "ls" => Ls(svc, json),
                "info" => Info(svc, reconciler, rest, json),
                "rm" or "remove" => Rm(svc, rest),
                "reconcile" or "doctor" => Reconcile(reconciler, rest, json),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Create(WorkspaceService svc, string[] args, bool json)
    {
        var repo = Args.TakeOption(ref args, "--repo")
            ?? throw new ArgumentException("create requires --repo <path>");
        var workspace = Args.FirstPositional(args)
            ?? throw new ArgumentException("create requires a workspace name");

        var record = svc.Create(repo, workspace);

        if (json) { WriteJson(record); return 0; }
        Console.WriteLine($"created workspace '{record.Workspace}'");
        foreach (var r in record.Repos)
            Console.WriteLine($"  {r.Name}: {r.WorktreePath}  [{r.Branch}]");
        Console.WriteLine($"  ports: {FormatPorts(record.Ports)}");
        return 0;
    }

    static int Ls(WorkspaceService svc, bool json)
    {
        var all = svc.List();
        if (json) { WriteJson(all); return 0; }
        if (all.Count == 0) { Console.WriteLine("no workspaces yet — create one with: sprig create <name> --repo <path>"); return 0; }

        Console.WriteLine($"{"WORKSPACE",-20} {"REPOS",-24} {"PORTS",-24} STATUS");
        foreach (var r in all.OrderBy(r => r.Workspace))
            Console.WriteLine($"{r.Workspace,-20} {string.Join(",", r.Repos.Select(x => x.Name)),-24} {FormatPorts(r.Ports),-24} {r.LastStatus}");
        return 0;
    }

    static int Info(WorkspaceService svc, WorkspaceReconciler reconciler, string[] args, bool json)
    {
        var workspace = Args.FirstPositional(args) ?? throw new ArgumentException("info requires a workspace name");
        var record = svc.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var report = reconciler.Inspect(workspace);

        if (json) { WriteJson(new { record, drift = report }); return 0; }

        Console.WriteLine($"workspace: {record.Workspace}   status: {record.LastStatus}   created: {record.CreatedAt:u}");
        Console.WriteLine($"ports: {FormatPorts(record.Ports)}");
        foreach (var r in record.Repos)
        {
            var state = report?.Repos.FirstOrDefault(x => x.WorktreePath == r.WorktreePath)?.State;
            Console.WriteLine($"  {r.Name}");
            Console.WriteLine($"    worktree: {r.WorktreePath}  [{state}]");
            Console.WriteLine($"    branch:   {r.Branch}");
        }
        return 0;
    }

    static int Rm(WorkspaceService svc, string[] args)
    {
        var force = Args.TakeFlag(ref args, "--force");
        var yes = Args.TakeFlag(ref args, "--yes");
        var workspace = Args.FirstPositional(args) ?? throw new ArgumentException("rm requires a workspace name");
        if (!yes)
        {
            Console.Error.WriteLine($"refusing to remove '{workspace}' without --yes");
            return 1;
        }
        svc.Remove(workspace, force);
        Console.WriteLine($"removed '{workspace}'{(force ? " (including branch)" : "")}");
        return 0;
    }

    static int Reconcile(WorkspaceReconciler reconciler, string[] args, bool json)
    {
        var repair = Args.TakeFlag(ref args, "--repair");
        var one = Args.FirstPositional(args);

        var reports = one is null
            ? reconciler.InspectAll()
            : reconciler.Inspect(one) is { } r ? [r] : throw new ArgumentException($"unknown workspace '{one}'");

        if (json) { WriteJson(reports); }
        else if (reports.Count == 0) Console.WriteLine("no workspaces to check");
        else
            foreach (var report in reports)
            {
                var flag = report.IsHealthy ? "ok" : report.HasDrift ? "DRIFT" : "gone";
                Console.WriteLine($"[{flag}] {report.Workspace}");
                foreach (var repo in report.Repos)
                    Console.WriteLine($"    {repo.RepoName}: {repo.State}  ({repo.WorktreePath})");
            }

        if (repair)
            foreach (var report in reports.Where(r => r.HasDrift))
                foreach (var action in reconciler.Repair(report.Workspace))
                    Console.WriteLine($"repaired: {action}");
        return 0;
    }

    static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command} (try --help)");
        return 1;
    }

    static string FormatPorts(IReadOnlyDictionary<string, int> ports)
        => ports.Count == 0 ? "-" : string.Join(",", ports.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    static void WriteJson<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    static string Version()
        => typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    static int Help()
    {
        Console.WriteLine($"""
            sprig {Version()} — worktree + infrastructure isolation (dev harness)

            USAGE:
                sprig <command> [options] [--json]

            COMMANDS:
                create <name> --repo <path>   Create an isolated workspace from a repo
                ls                            List workspaces
                info <name>                   Show a workspace's repos, ports, drift
                rm <name> [--force] [--yes]   Tear down a workspace (--force also deletes the branch)
                reconcile [<name>] [--repair] Detect (and optionally repair) drift
                doctor                        Alias for reconcile over all workspaces

            OPTIONS:
                --json           Machine-readable output
                -h, --help       Show this help
                -v, --version    Show version
            """);
        return 0;
    }
}

/// <summary>Minimal arg helpers for the harness (not a full parser).</summary>
static class Args
{
    public static bool TakeFlag(ref string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        if (idx < 0) return false;
        args = args.Where((_, i) => i != idx).ToArray();
        return true;
    }

    public static string? TakeOption(ref string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        if (idx < 0 || idx + 1 >= args.Length) return null;
        var value = args[idx + 1];
        args = args.Where((_, i) => i != idx && i != idx + 1).ToArray();
        return value;
    }

    public static string? FirstPositional(string[] args)
        => args.FirstOrDefault(a => !a.StartsWith('-'));
}
