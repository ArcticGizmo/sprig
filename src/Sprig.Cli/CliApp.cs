using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Init;
using Sprig.Core.Planning;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Settings;
using Sprig.Core.Shared;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
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
        var runner = new ProcessRunner();
        var git = new GitService(runner);
        var ports = new FilePortStore(paths, new FileSettingsStore(paths));
        var instances = new InstanceStore(paths);
        var sharedStore = new SharedResourceStore(paths);
        var svc = new WorkspaceService(git, ports, instances, new EnvClobberService(),
            new ComposeGenerator(), new DockerService(runner), paths, new SetupRunner(runner), sharedStore);
        var reconciler = new WorkspaceReconciler(git, instances);
        var registry = new RepoRegistryStore(paths);
        var stacks = new StackStore(paths, registry, instances);
        var resolver = new StackResolver(registry, stacks, git);

        var command = args[0];
        var rest = args[1..];
        try
        {
            return command switch
            {
                "create" => Create(svc, resolver, rest, json),
                "plan" => Plan(svc, resolver, rest, json),
                "shared" => SharedCmd(sharedStore, rest, json),
                "ls" => Ls(svc, json),
                "info" => Info(svc, reconciler, rest, json),
                "rm" or "remove" => Rm(svc, rest),
                "up" => Up(svc, rest),
                "down" => Down(svc, rest),
                "reset" => Reset(svc, rest),
                "status" => Status(svc, rest, json),
                "reconcile" or "doctor" => Reconcile(reconciler, rest, json),
                "repo" => Repo(registry, rest, json),
                "stack" => Stack(stacks, rest, json),
                "templates" => Templates(stacks, json),
                "init" => Init(git, registry, rest, json),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Create(WorkspaceService svc, StackResolver resolver, string[] args, bool json)
    {
        var noShared = Args.TakeFlag(ref args, "--no-shared");
        var stackName = Args.TakeOption(ref args, "--stack");
        var repo = Args.TakeOption(ref args, "--repo");
        var workspace = Args.FirstPositional(args)
            ?? throw new ArgumentException("create requires a workspace name");
        var options = new CreateOptions { NoShared = noShared };

        var record = stackName is not null ? svc.Create(resolver.Resolve(stackName), workspace, null, options)
            : repo is not null ? svc.Create(svc.ResolveSingleRepo(repo), workspace, null, options)
            : throw new ArgumentException("create requires --stack <name> or --repo <path>");

        if (json) { WriteJson(record); return 0; }
        Console.WriteLine($"created workspace '{record.Workspace}'{(record.Stack is { } st ? $" from stack '{st}'" : "")}");
        if (record.Ports.Count > 0)
            Console.WriteLine($"  ports: {FormatPorts(record.Ports)}");
        foreach (var r in record.Repos)
        {
            Console.WriteLine($"  {r.Name}: {r.WorktreePath}  [{r.Branch}]");
            if (r.Inputs.Count > 0)
                Console.WriteLine($"    inputs: {FormatKv(r.Inputs)}");
            foreach (var s in r.Setup)
            {
                Console.WriteLine($"    setup {(s.Success ? "✓" : "✗")} {s.Command}{(s.Success ? "" : $" (exit {s.ExitCode})")}");
                if (!s.Success && !string.IsNullOrWhiteSpace(s.Output))
                    foreach (var line in s.Output.TrimEnd().Split('\n'))
                        Console.WriteLine($"      {line.TrimEnd()}");
            }
        }
        if (record.Repos.Any(r => r.Setup.Any(s => !s.Success)))
            Console.WriteLine("  note: a setup command failed — the workspace was kept; finish setup manually in the worktree.");
        return 0;
    }

    /// <summary>
    /// Print every value a workspace resolves to, and which layer produced it. With <c>--stack</c> this is
    /// a dry run — nothing is allocated or written, so ports show as <c>{name}</c> placeholders. Given an
    /// existing workspace it re-plans against the ports that workspace actually holds.
    /// </summary>
    static int Plan(WorkspaceService svc, StackResolver resolver, string[] args, bool json)
    {
        var options = new CreateOptions { NoShared = Args.TakeFlag(ref args, "--no-shared") };
        var stackName = Args.TakeOption(ref args, "--stack");
        var repo = Args.TakeOption(ref args, "--repo");
        var name = Args.TakeOption(ref args, "--name") ?? Args.FirstPositional(args);

        BoundPlan plan;
        if (stackName is not null || repo is not null)
        {
            var stack = stackName is not null ? resolver.Resolve(stackName) : svc.ResolveSingleRepo(repo!);
            plan = svc.PreviewPlan(stack, name ?? "preview", options);
        }
        else
        {
            var workspace = name ?? throw new ArgumentException(
                "plan requires a workspace name, or --stack <name> / --repo <path> for a dry run");
            var record = svc.Get(workspace)
                ?? throw new ArgumentException($"unknown workspace '{workspace}'");
            var stack = record.Stack is { } s ? resolver.Resolve(s)
                : throw new ArgumentException(
                    $"workspace '{workspace}' wasn't created from a stack — nothing to re-plan");
            plan = svc.ExplainPlan(stack, workspace, options);
        }

        if (json) { WriteJson(plan); return 0; }

        var allocated = plan.Ports.Count > 0;
        Console.WriteLine($"plan for '{plan.Workspace}'{(plan.StackName is { } st ? $" from stack '{st}'" : "")}"
            + (allocated ? "" : "  (dry run — no ports allocated)"));

        foreach (var note in plan.Notes.Where(n => n.Repo is null))
            Console.WriteLine($"  {Layer(note.Layer)} {note.Target,-28} {note.Value}");
        foreach (var port in plan.UnreferencedPorts)
            Console.WriteLine($"  {"-",-8} {PlanTargets.Port(port),-28} not allocated — nothing references it");

        foreach (var boundRepo in plan.Repos)
        {
            Console.WriteLine($"  {boundRepo.Name}");
            foreach (var note in plan.NotesFor(boundRepo.Name))
            {
                Console.WriteLine($"    {Layer(note.Layer)} {note.Target,-26} {note.Value}");
                if (note.Expression is { } expr)
                    Console.WriteLine($"    {"",-8} {"",-26} from {expr}");
                if (note.Replaced is { } was)
                    Console.WriteLine($"    {"",-8} {"",-26} was  {was}"
                        + (note.Source is { } src ? $"  (overridden by {src})" : ""));
            }
        }
        return 0;
    }

    static string Layer(PlanLayer layer) => layer switch
    {
        PlanLayer.Repo => "[repo]  ",
        PlanLayer.Stack => "[stack] ",
        PlanLayer.Shared => "[shared]",
        _ => "[?]     ",
    };

    /// <summary>Machine-local shared resources: the overlays that pool infrastructure across workspaces.</summary>
    static int SharedCmd(SharedResourceStore store, string[] args, bool json)
    {
        var sub = Args.FirstPositional(args) ?? "ls";
        var tail = args.Where(a => a != sub).ToArray();
        switch (sub)
        {
            case "ls":
                var all = store.List();
                if (json) { WriteJson(all); return 0; }
                if (all.Count == 0)
                {
                    Console.WriteLine("no shared resources defined");
                    return 0;
                }
                foreach (var r in all)
                    Console.WriteLine($"  {(r.Enabled ? "on " : "off")} {r.Name,-22} capacity {r.Capacity}  " +
                                      $"injects {string.Join(", ", r.Injects.Select(i => i.Repo))}");
                return 0;

            case "show":
                var showName = Args.FirstPositional(tail) ?? throw new ArgumentException("shared show requires a name");
                var res = store.Get(showName) ?? throw new ArgumentException($"unknown shared resource '{showName}'");
                if (json) { WriteJson(res); return 0; }
                Console.WriteLine($"{res.Name}  {(res.Enabled ? "enabled" : "disabled")}  capacity {res.Capacity}  whenIdle {res.WhenIdle}");
                foreach (var (k, v) in res.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
                    Console.WriteLine($"  value  {k,-14} {v}");
                foreach (var inject in res.Injects)
                {
                    Console.WriteLine($"  injects into {inject.Repo}");
                    foreach (var (k, v) in inject.Inputs)
                        Console.WriteLine($"    [stack]  input  {k,-24} {v}");
                    foreach (var e in inject.Env)
                        foreach (var (k, v) in e.Set)
                            Console.WriteLine($"    [repo]   env    {e.File}#{k,-16} {v}");
                    foreach (var c in inject.Compose)
                        foreach (var o in c.Overrides)
                            Console.WriteLine($"    [repo]   compose {c.File}#{string.Join('.', o.Path),-14} {o.Template}");
                    foreach (var sup in inject.Suppress)
                        foreach (var svcName in sup.Services)
                            Console.WriteLine($"    [repo]   suppress {sup.File}#services.{svcName}");
                }
                return 0;

            case "enable":
            case "disable":
                var toggleName = Args.FirstPositional(tail) ?? throw new ArgumentException($"shared {sub} requires a name");
                var toggle = store.Get(toggleName) ?? throw new ArgumentException($"unknown shared resource '{toggleName}'");
                store.Save(toggle with { Enabled = sub == "enable" });
                Console.WriteLine($"{sub}d '{toggleName}'");
                return 0;

            case "rm":
                var rmName = Args.FirstPositional(tail) ?? throw new ArgumentException("shared rm requires a name");
                store.Remove(rmName);
                Console.WriteLine($"removed shared resource '{rmName}'");
                return 0;

            default:
                Console.Error.WriteLine($"unknown shared subcommand: {sub} (ls, show, enable, disable, rm)");
                return 1;
        }
    }

    static int Repo(RepoRegistryStore registry, string[] args, bool json)
    {
        var sub = Args.FirstPositional(args) ?? "ls";
        var tail = args.Where(a => a != sub).ToArray();
        switch (sub)
        {
            case "add":
                var name = Args.TakeOption(ref tail, "--name");
                var path = Args.FirstPositional(tail) ?? throw new ArgumentException("repo add requires a path");
                var added = registry.Add(path, name);
                Console.WriteLine($"registered '{added.Name}' -> {added.Path}");
                return 0;
            case "ls":
                var repos = registry.List();
                if (json) { WriteJson(repos); return 0; }
                if (repos.Count == 0) { Console.WriteLine("no repos registered"); return 0; }
                foreach (var r in repos) Console.WriteLine($"  {r.Name,-24} {r.Path}");
                return 0;
            case "rm":
                var target = Args.FirstPositional(tail) ?? throw new ArgumentException("repo rm requires a name");
                registry.Remove(target);
                Console.WriteLine($"unregistered '{target}'");
                return 0;
            default:
                Console.Error.WriteLine($"unknown repo subcommand '{sub}' (add|ls|rm)");
                return 1;
        }
    }

    static int Stack(StackStore stacks, string[] args, bool json)
    {
        var sub = Args.FirstPositional(args) ?? "ls";
        var tail = args.Where(a => a != sub).ToArray();
        switch (sub)
        {
            case "create":
                var reposCsv = Args.TakeOption(ref tail, "--repos")
                    ?? throw new ArgumentException("stack create requires --repos a,b");
                var portList = Args.TakeAll(ref tail, "--port");
                var bindings = ParseBindings(Args.TakeAll(ref tail, "--bind"));
                var name = Args.FirstPositional(tail) ?? throw new ArgumentException("stack create requires a name");
                stacks.Save(new StackDefinition
                {
                    Name = name,
                    Repos = reposCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Ports = portList,
                    Bindings = bindings,
                });
                Console.WriteLine($"created stack '{name}'");
                return 0;
            case "ls":
                var all = stacks.List();
                if (json) { WriteJson(all); return 0; }
                if (all.Count == 0) { Console.WriteLine("no stacks defined"); return 0; }
                foreach (var s in all) Console.WriteLine($"  {s.Name,-20} {string.Join(", ", s.Repos)}");
                return 0;
            case "show":
                var showName = Args.FirstPositional(tail) ?? throw new ArgumentException("stack show requires a name");
                var stack = stacks.Get(showName) ?? throw new ArgumentException($"unknown stack '{showName}'");
                WriteJson(stack);
                return 0;
            case "rm":
                var rmName = Args.FirstPositional(tail) ?? throw new ArgumentException("stack rm requires a name");
                stacks.Remove(rmName);
                Console.WriteLine($"removed stack '{rmName}'");
                return 0;
            case "export":
                var exp = tail.Where(a => !a.StartsWith('-')).ToArray();
                if (exp.Length < 2) throw new ArgumentException("stack export requires <name> <path>");
                Console.WriteLine($"exported to {stacks.Export(exp[0], exp[1])}");
                return 0;
            case "import":
                var importPath = Args.FirstPositional(tail) ?? throw new ArgumentException("stack import requires a path");
                var imported = stacks.Import(importPath);
                Console.WriteLine($"imported stack '{imported.Name}'");
                return 0;
            default:
                Console.Error.WriteLine($"unknown stack subcommand '{sub}' (create|ls|show|rm|export|import)");
                return 1;
        }
    }

    static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static int Init(IGitService git, RepoRegistryStore registry, string[] args, bool json)
    {
        var print = Args.TakeFlag(ref args, "--print");
        var force = Args.TakeFlag(ref args, "--force");
        var register = Args.TakeFlag(ref args, "--register");
        var repo = Args.TakeOption(ref args, "--repo") ?? Args.FirstPositional(args) ?? Environment.CurrentDirectory;
        var root = Path.GetFullPath(repo);

        if (!Directory.Exists(root))
            throw new ArgumentException($"path does not exist: {root}");

        var proposal = new InitInspector(git).Inspect(root);
        var text = JsonSerializer.Serialize(proposal.Config, ConfigJsonOptions);

        if (json) { WriteJson(proposal); return 0; }

        foreach (var note in proposal.Notes)
            Console.WriteLine($"note: {note}");

        if (print)
        {
            Console.WriteLine(text);
            return 0;
        }

        var target = Path.Combine(root, ".sprig.json");
        if (File.Exists(target) && !force)
        {
            Console.Error.WriteLine($".sprig.json already exists at {target} — pass --force to overwrite, or --print to preview");
            return 1;
        }

        File.WriteAllText(target, text + "\n");
        Console.WriteLine($"wrote {target}");

        if (register)
        {
            var added = registry.Add(root);
            Console.WriteLine($"registered '{added.Name}'");
        }
        else
        {
            Console.WriteLine($"next: sprig repo add \"{root}\"   (then add it to a stack, or: sprig create <name> --repo \"{root}\")");
        }
        return 0;
    }

    // Parse "--bind repo:input=expr" args into Bindings[repo][input] = expr.
    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseBindings(IReadOnlyList<string> binds)
    {
        var raw = new Dictionary<string, Dictionary<string, string>>();
        foreach (var b in binds)
        {
            var colon = b.IndexOf(':');
            var eq = colon < 0 ? -1 : b.IndexOf('=', colon + 1);
            if (colon <= 0 || eq < 0)
                throw new ArgumentException($"--bind must be repo:input=expr, got '{b}'");
            var repo = b[..colon];
            var input = b[(colon + 1)..eq];
            var expr = b[(eq + 1)..];
            if (!raw.TryGetValue(repo, out var d)) raw[repo] = d = new Dictionary<string, string>();
            d[input] = expr;
        }
        return raw.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }

    static int Templates(StackStore stacks, bool json)
    {
        var all = stacks.List();
        if (json) { WriteJson(all); return 0; }
        if (all.Count == 0) { Console.WriteLine("no templates (stacks) defined"); return 0; }
        Console.WriteLine($"{"TEMPLATE",-20} REPOS");
        foreach (var s in all) Console.WriteLine($"{s.Name,-20} {string.Join(", ", s.Repos)}");
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

    static int Up(WorkspaceService svc, string[] args)
    {
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("up requires a workspace name");
        svc.Up(ws);
        Console.WriteLine($"infra up for '{ws}'");
        return 0;
    }

    static int Down(WorkspaceService svc, string[] args)
    {
        var volumes = Args.TakeFlag(ref args, "--volumes");
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("down requires a workspace name");
        svc.Down(ws, volumes);
        Console.WriteLine($"infra down for '{ws}'{(volumes ? " (volumes removed)" : "")}");
        return 0;
    }

    static int Reset(WorkspaceService svc, string[] args)
    {
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("reset requires a workspace name");
        svc.Reset(ws);
        Console.WriteLine($"infra reset for '{ws}'");
        return 0;
    }

    static int Status(WorkspaceService svc, string[] args, bool json)
    {
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("status requires a workspace name");
        var containers = svc.Status(ws);
        if (json) { WriteJson(containers); return 0; }
        if (containers.Count == 0) { Console.WriteLine("no containers running"); return 0; }
        foreach (var c in containers)
            Console.WriteLine($"  {c.Name}  {c.State}");
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

    static string FormatKv(IReadOnlyDictionary<string, string> kv)
        => kv.Count == 0 ? "-" : string.Join("  ", kv.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

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
                create <name> --stack <s> | --repo <path> [--no-shared]   Create an isolated workspace
                plan <name> | --stack <s> | --repo <path> [--no-shared]   Show every value and the layer that set it
                shared ls | show <name> | enable <name> | disable <name> | rm <name>
                                              Machine-local pooled infrastructure (overlays)
                ls                            List workspaces
                info <name>                   Show a workspace's repos, ports, drift
                up <name>                     Bring the workspace's docker infra up
                down <name> [--volumes]       Stop infra (--volumes also wipes data)
                reset <name>                  Restart infra (down then up)
                status <name>                 Live container status
                rm <name> [--force] [--yes]   Tear down a workspace (--force also deletes the branch)
                reconcile [<name>] [--repair] Detect (and optionally repair) drift
                doctor                        Alias for reconcile over all workspaces
                init [--repo <path>] [--print] [--force] [--register]   Detect & propose .sprig.json
                repo add <path> [--name x]    Register a repo (also: repo ls, repo rm <name>)
                stack create <name> --repos a,b [--port p] [--bind repo:input=expr]   Define a stack
                stack ls | show <name> | rm <name> | export <name> <path> | import <path>
                templates                     List stacks and their repos

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

    /// <summary>Take every <c>--name value</c> pair (repeated flags), returning the values.</summary>
    public static List<string> TakeAll(ref string[] args, string name)
    {
        var values = new List<string>();
        string? v;
        while ((v = TakeOption(ref args, name)) is not null) values.Add(v);
        return values;
    }

    public static string? FirstPositional(string[] args)
        => args.FirstOrDefault(a => !a.StartsWith('-'));
}
