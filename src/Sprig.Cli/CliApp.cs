using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Init;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Settings;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Cli;

/// <summary>The command dispatcher over Sprig.Core — the CLI's whole surface. A supported,
/// shipped front-end (not a dev-only harness); <c>--json</c> output is a stability contract.</summary>
public static class CliApp
{
    public static int Run(string[] args)
    {
        // SPRIG_STORE points the central store somewhere other than %LOCALAPPDATA%\sprig — useful for
        // running against a throwaway store and the seam the CLI tests drive the dispatcher through.
        var root = Environment.GetEnvironmentVariable("SPRIG_STORE");
        return Run(args, new SprigPaths(string.IsNullOrWhiteSpace(root) ? null : root));
    }

    internal static int Run(string[] args, ISprigPaths paths)
    {
        var json = Args.TakeFlag(ref args, "--json");
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Help();
        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine(Version());
            return 0;
        }

        var runner = new ProcessRunner();
        var git = new GitService(runner);
        var ports = new FilePortStore(paths, new FileSettingsStore(paths));
        var instances = new InstanceStore(paths);
        var svc = new WorkspaceService(git, ports, instances, new EnvClobberService(),
            new ComposeGenerator(), new DockerService(runner), paths, new SetupRunner(runner));
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
                "open" => Open(rest),
                "update" => Update(rest),
                "create" => Create(svc, resolver, stacks, rest, json),
                "ls" => Ls(svc, json),
                "info" => Info(svc, reconciler, rest, json),
                "rm" or "remove" => Rm(svc, rest, json),
                "up" => Up(svc, rest, json),
                "down" => Down(svc, rest, json),
                "reset" => Reset(svc, rest, json),
                "status" => Status(svc, rest, json),
                "reconcile" or "doctor" => Reconcile(reconciler, rest, json),
                "repo" => Repo(registry, rest, json),
                "stack" => Stack(stacks, rest, json),
                "settings" or "config" => Settings(new FileSettingsStore(paths), rest, json),
                "templates" => Templates(stacks, json),
                "init" => Init(git, registry, rest, json),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            // Machine callers get a parseable failure ({ ok: false, error }); humans get the same
            // message on stderr. Either way the exit code is 1 — the primary signal for scripts.
            if (json) WriteJson(new { ok = false, error = ex.Message });
            else Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // Launch the desktop app (sprig-gui) that ships alongside the CLI — the escape hatch to the GUI
    // when you want something more granular than the terminal offers. Detaches (UseShellExecute, no
    // wait) so the terminal is handed straight back, exactly as if you'd double-clicked the app.
    static int Open(string[] args)
    {
        var exe = LocateGui() ?? throw new FileNotFoundException(
            "could not find the sprig app (sprig-gui). Install sprig via the installer so both " +
            "ship together, or build Sprig.App alongside the CLI.");

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Console.WriteLine("opening sprig…");
        return 0;
    }

    // Install a newer release in place (or, with --check, just report whether one exists). Delegates
    // to CliUpdater, which drives Velopack's check/download/apply against the same feed as the app.
    static int Update(string[] args)
    {
        var check = Args.TakeFlag(ref args, "--check");
        return CliUpdater.Run(check);
    }

    // In the Velopack package sprig-gui(.exe) sits in the same directory as sprig(.exe), so look
    // beside ourselves first. Failing that (a dev build, where each project has its own bin output),
    // swap the Sprig.Cli segment of our path for Sprig.App — same bin/<config>/<tfm> layout either
    // side — and probe there. Covers both worlds with no env var or config to set.
    static string? LocateGui()
    {
        var exeName = OperatingSystem.IsWindows() ? "sprig-gui.exe" : "sprig-gui";
        var baseDir = AppContext.BaseDirectory;

        foreach (var dir in new[] { baseDir, baseDir.Replace("Sprig.Cli", "Sprig.App") })
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static int Create(WorkspaceService svc, StackResolver resolver, StackStore stacks, string[] args, bool json)
    {
        var stackName = Args.TakeOption(ref args, "--stack");
        var repo = Args.TakeOption(ref args, "--repo");
        var only = Args.TakeList(ref args, "--only");
        var without = Args.TakeList(ref args, "--without");
        var workspace = Args.FirstPositional(args)
            ?? throw new ArgumentException("create requires a workspace name");

        if ((only.Count > 0 || without.Count > 0) && stackName is null)
            throw new ArgumentException("--only/--without narrow a stack — they need --stack <name>");

        var record = stackName is not null
                ? svc.Create(resolver.Resolve(stackName, Selection(stacks, stackName, only, without)), workspace)
            : repo is not null ? svc.Create(repo, workspace)
            : throw new ArgumentException("create requires --stack <name> or --repo <path>");

        if (json) { WriteJson(record); return 0; }
        Console.WriteLine($"created workspace '{record.Workspace}'{(record.Stack is { } st ? $" from stack '{st}'" : "")}");
        if (record.IsPartial)
            Console.WriteLine($"  partial: without {string.Join(", ", record.ExcludedRepos)}" +
                (record.SkippedPorts.Count > 0 ? $"; ports not provisioned: {string.Join(", ", record.SkippedPorts)}" : ""));
        if (record.Ports.Count > 0)
            Console.WriteLine($"  ports: {FormatPorts(record.Ports)}");
        foreach (var r in record.Repos)
        {
            Console.WriteLine($"  {r.Name}: {r.WorktreePath}  [{r.Branch}]");
            if (r.Inputs.Count > 0)
                Console.WriteLine($"    inputs: {FormatKv(r.Inputs)}");
            // Group setup by module; only show module headers when a repo has more than one, so a
            // single-module (or legacy) repo prints exactly as before.
            var setupByModule = r.Setup.GroupBy(s => s.Module).ToList();
            var showModuleHeaders = setupByModule.Count > 1;
            foreach (var group in setupByModule)
            {
                if (showModuleHeaders && group.Key is { } module)
                    Console.WriteLine($"    module {module}:");
                var indent = showModuleHeaders ? "      " : "    ";
                foreach (var s in group)
                {
                    Console.WriteLine($"{indent}setup {(s.Success ? "✓" : "✗")} {s.Command}{(s.Success ? "" : $" (exit {s.ExitCode})")}");
                    if (!s.Success && !string.IsNullOrWhiteSpace(s.Output))
                        foreach (var line in s.Output.TrimEnd().Split('\n'))
                            Console.WriteLine($"{indent}  {line.TrimEnd()}");
                }
            }
        }
        if (record.Repos.Any(r => r.Setup.Any(s => !s.Success)))
            Console.WriteLine("  note: a setup command failed — the workspace was kept; finish setup manually in the worktree.");
        return 0;
    }

    /// <summary>Turn <c>--only</c>/<c>--without</c> into the repo subset to create (null = the whole
    /// stack). <c>--without</c> is resolved against the stack's repo list so the two flags are
    /// interchangeable ways of saying the same thing; naming an unknown repo is an error either way.</summary>
    static IReadOnlyList<string>? Selection(StackStore stacks, string stackName,
        List<string> only, List<string> without)
    {
        if (only.Count == 0 && without.Count == 0) return null;
        var stack = stacks.Get(stackName) ?? throw new StackException($"unknown stack '{stackName}'");

        // Validate both lists against the stack (Include does it for --only; do the same for --without
        // so a typo'd exclusion fails loudly instead of silently keeping every repo).
        var included = StackSelection.Include(stack, only.Count > 0 ? only : null);
        var unknown = without.Where(r => !stack.Repos.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new StackException(
                $"stack '{stackName}' has no repo{(unknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unknown.Select(r => $"'{r}'"))} " +
                $"(it has: {string.Join(", ", stack.Repos)})");

        var drop = new HashSet<string>(without, StringComparer.Ordinal);
        var selection = included.Where(r => !drop.Contains(r)).ToList();
        if (selection.Count == 0)
            throw new StackException($"that leaves no repos to create from stack '{stackName}'");
        return selection;
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
                return Ok(json, $"registered '{added.Name}' -> {added.Path}",
                    new { ok = true, name = added.Name, path = added.Path });
            case "ls":
                var repos = registry.List();
                if (json) { WriteJson(repos); return 0; }
                if (repos.Count == 0) { Console.WriteLine("no repos registered"); return 0; }
                foreach (var r in repos) Console.WriteLine($"  {r.Name,-24} {r.Path}");
                return 0;
            case "rm":
                var target = Args.FirstPositional(tail) ?? throw new ArgumentException("repo rm requires a name");
                registry.Remove(target);
                return Ok(json, $"unregistered '{target}'", new { ok = true, name = target, action = "remove" });
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
                if (stacks.Get(name) is not null)
                    throw new ArgumentException($"stack '{name}' already exists — use 'stack edit {name}' to change it");
                var created = new StackDefinition
                {
                    Name = name,
                    Repos = reposCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Ports = portList,
                    Bindings = bindings,
                };
                // Populate the shared-port overlay from the bindings so a CLI-built stack shows its
                // shares in the app (and passes the store's share/binding consistency check).
                stacks.Save(created with { Shares = StackMigration.DeriveShares(created) });
                return Ok(json, $"created stack '{name}'", new { ok = true, name, action = "create" });
            case "edit":
                var editName = Args.FirstPositional(tail) ?? throw new ArgumentException("stack edit requires a name");
                var current = stacks.Get(editName)
                    ?? throw new ArgumentException($"unknown stack '{editName}' — use 'stack create' to make one");
                var reposOpt = Args.TakeOption(ref tail, "--repos");
                var portsOpt = Args.TakeAll(ref tail, "--port");
                var bindOpt = ParseBindings(Args.TakeAll(ref tail, "--bind"));
                // Each facet is replaced only if its flag was supplied; bindings merge onto the
                // existing set (a repeated input overrides, others are kept). Shares are re-derived.
                var edited = current with
                {
                    Repos = reposOpt is not null
                        ? reposOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : current.Repos,
                    Ports = portsOpt.Count > 0 ? portsOpt : current.Ports,
                    Bindings = MergeBindings(current.Bindings, bindOpt),
                };
                stacks.Save(edited with { Shares = StackMigration.DeriveShares(edited) });
                return Ok(json, $"updated stack '{editName}'", new { ok = true, name = editName, action = "edit" });
            case "ls":
                var all = stacks.List();
                if (json) { WriteJson(all); return 0; }
                if (all.Count == 0) { Console.WriteLine("no stacks defined"); return 0; }
                foreach (var s in all) Console.WriteLine($"  {s.Name,-20} {string.Join(", ", s.Repos)}");
                return 0;
            case "show":
                var showName = Args.FirstPositional(tail) ?? throw new ArgumentException("stack show requires a name");
                var stack = stacks.Get(showName) ?? throw new ArgumentException($"unknown stack '{showName}'");
                if (json) { WriteJson(stack); return 0; }
                PrintStack(stack);
                return 0;
            case "rm":
                var rmName = Args.FirstPositional(tail) ?? throw new ArgumentException("stack rm requires a name");
                stacks.Remove(rmName);
                return Ok(json, $"removed stack '{rmName}'", new { ok = true, name = rmName, action = "remove" });
            case "export":
                var exp = tail.Where(a => !a.StartsWith('-')).ToArray();
                if (exp.Length < 2) throw new ArgumentException("stack export requires <name> <path>");
                var dest = stacks.Export(exp[0], exp[1]);
                return Ok(json, $"exported to {dest}", new { ok = true, name = exp[0], path = dest });
            case "import":
                var importPath = Args.FirstPositional(tail) ?? throw new ArgumentException("stack import requires a path");
                var imported = stacks.Import(importPath);
                return Ok(json, $"imported stack '{imported.Name}'", new { ok = true, name = imported.Name, action = "import" });
            default:
                Console.Error.WriteLine($"unknown stack subcommand '{sub}' (create|edit|ls|show|rm|export|import)");
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

        // --print previews without touching disk — the one read-only path. --json pairs with it to
        // get the proposal as a machine object; without --print, --json reports the write instead.
        if (print)
        {
            if (json) WriteJson(proposal);
            else
            {
                foreach (var note in proposal.Notes)
                    Console.WriteLine($"note: {note}");
                Console.WriteLine(text);
            }
            return 0;
        }

        var target = Path.Combine(root, ".sprig.json");
        if (File.Exists(target) && !force)
        {
            var msg = $".sprig.json already exists at {target} — pass --force to overwrite, or --print to preview";
            if (json) WriteJson(new { ok = false, error = msg });
            else Console.Error.WriteLine(msg);
            return 1;
        }

        File.WriteAllText(target, text + "\n");
        var registered = register ? registry.Add(root).Name : null;

        if (json)
        {
            WriteJson(new { ok = true, path = target, registered, notes = proposal.Notes });
            return 0;
        }

        foreach (var note in proposal.Notes)
            Console.WriteLine($"note: {note}");
        Console.WriteLine($"wrote {target}");
        if (registered is not null)
            Console.WriteLine($"registered '{registered}'");
        else
            Console.WriteLine($"next: sprig repo add \"{root}\"   (then add it to a stack, or: sprig create <name> --repo \"{root}\")");
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

    // Overlay override bindings onto the existing set: a repo/input present in both takes the new
    // expression; everything else is kept. Removal isn't expressed here — redefine with create instead.
    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MergeBindings(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> existing,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrides)
    {
        var merged = existing.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, string>(kv.Value.ToDictionary(x => x.Key, x => x.Value)));
        foreach (var repo in overrides)
        {
            if (!merged.TryGetValue(repo.Key, out var inputs))
                merged[repo.Key] = inputs = new Dictionary<string, string>();
            foreach (var input in repo.Value) inputs[input.Key] = input.Value;
        }
        return merged.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }

    static int Settings(ISettingsStore store, string[] args, bool json)
    {
        var sub = Args.FirstPositional(args) ?? "show";
        var tail = args.Where(a => a != sub).ToArray();
        var current = store.Get();
        switch (sub)
        {
            case "show":
                // Project just the port-allocation policy — the app-internal fields (changelog
                // flags, completed guides, …) aren't the CLI's contract and would only leak.
                if (json)
                {
                    WriteJson(new
                    {
                        portRangeStart = current.PortRangeStart,
                        portRangeEndExclusive = current.PortRangeEndExclusive,
                        restrictedPorts = current.RestrictedPorts,
                    });
                    return 0;
                }
                Console.WriteLine($"port range:       {current.PortRangeStart}..{current.PortRangeEndExclusive} (end exclusive)");
                Console.WriteLine($"restricted ports: {(current.RestrictedPorts.Count == 0 ? "-" : string.Join(", ", current.RestrictedPorts))}");
                return 0;
            case "set":
                var start = Args.TakeOption(ref tail, "--start");
                var end = Args.TakeOption(ref tail, "--end");
                var restrict = Args.TakeList(ref tail, "--restrict");
                var unrestrict = Args.TakeList(ref tail, "--unrestrict");

                var updated = current.Clone();
                if (start is not null) updated.PortRangeStart = ParsePort(start, "--start");
                if (end is not null) updated.PortRangeEndExclusive = ParsePort(end, "--end");
                var ports = new SortedSet<int>(updated.RestrictedPorts);
                foreach (var p in restrict) ports.Add(ParsePort(p, "--restrict"));
                foreach (var p in unrestrict) ports.Remove(ParsePort(p, "--unrestrict"));
                updated.RestrictedPorts = ports.ToList();

                store.Save(updated); // validates the range and restricted ports
                return Ok(json, "settings updated", new
                {
                    ok = true,
                    portRangeStart = updated.PortRangeStart,
                    portRangeEndExclusive = updated.PortRangeEndExclusive,
                    restrictedPorts = updated.RestrictedPorts,
                });
            default:
                Console.Error.WriteLine($"unknown settings subcommand '{sub}' (show|set)");
                return 1;
        }
    }

    static int ParsePort(string value, string flag)
        => int.TryParse(value, out var n) ? n : throw new ArgumentException($"{flag} must be a number, got '{value}'");

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
        if (record.IsPartial)
        {
            Console.WriteLine($"partial: stack '{record.Stack}' without {string.Join(", ", record.ExcludedRepos)}");
            if (record.SkippedPorts.Count > 0)
                Console.WriteLine($"  ports not provisioned: {string.Join(", ", record.SkippedPorts)}");
        }
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

    static int Rm(WorkspaceService svc, string[] args, bool json)
    {
        var force = Args.TakeFlag(ref args, "--force");
        var yes = Args.TakeFlag(ref args, "--yes");
        var workspace = Args.FirstPositional(args) ?? throw new ArgumentException("rm requires a workspace name");
        if (!yes)
        {
            var msg = $"refusing to remove '{workspace}' without --yes";
            if (json) WriteJson(new { ok = false, error = msg });
            else Console.Error.WriteLine(msg);
            return 1;
        }
        svc.Remove(workspace, force);
        return Ok(json, $"removed '{workspace}'{(force ? " (including branch)" : "")}",
            new { ok = true, workspace, action = "remove", branchDeleted = force });
    }

    static int Up(WorkspaceService svc, string[] args, bool json)
    {
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("up requires a workspace name");
        svc.Up(ws);
        return Ok(json, $"infra up for '{ws}'", new { ok = true, workspace = ws, action = "up" });
    }

    static int Down(WorkspaceService svc, string[] args, bool json)
    {
        var volumes = Args.TakeFlag(ref args, "--volumes");
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("down requires a workspace name");
        svc.Down(ws, volumes);
        return Ok(json, $"infra down for '{ws}'{(volumes ? " (volumes removed)" : "")}",
            new { ok = true, workspace = ws, action = "down", volumesRemoved = volumes });
    }

    static int Reset(WorkspaceService svc, string[] args, bool json)
    {
        var ws = Args.FirstPositional(args) ?? throw new ArgumentException("reset requires a workspace name");
        svc.Reset(ws);
        return Ok(json, $"infra reset for '{ws}'", new { ok = true, workspace = ws, action = "reset" });
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

        // Run repairs first (when asked) so both output paths can report what was done — the JSON
        // path folds them into one object rather than trailing plain text after the blob.
        var repairs = repair
            ? reports.Where(r => r.HasDrift)
                .Select(r => new { workspace = r.Workspace, actions = reconciler.Repair(r.Workspace) })
                .ToList()
            : null;

        if (json)
        {
            if (repairs is null) WriteJson(reports);
            else WriteJson(new { reports, repairs });
            return 0;
        }

        if (reports.Count == 0) Console.WriteLine("no workspaces to check");
        else
            foreach (var report in reports)
            {
                var flag = report.IsHealthy ? "ok" : report.HasDrift ? "DRIFT" : "gone";
                Console.WriteLine($"[{flag}] {report.Workspace}");
                foreach (var repo in report.Repos)
                    Console.WriteLine($"    {repo.RepoName}: {repo.State}  ({repo.WorktreePath})");
            }

        if (repairs is not null)
            foreach (var fix in repairs)
                foreach (var action in fix.actions)
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

    // Human-readable stack dump for `stack show` (the --json path serialises the record instead).
    static void PrintStack(StackDefinition stack)
    {
        Console.WriteLine($"stack: {stack.Name}");
        Console.WriteLine($"  repos: {string.Join(", ", stack.Repos)}");
        Console.WriteLine($"  ports: {(stack.Ports.Count == 0 ? "-" : string.Join(", ", stack.Ports))}");
        foreach (var repo in stack.Bindings.OrderBy(b => b.Key))
        {
            Console.WriteLine($"  {repo.Key}:");
            foreach (var input in repo.Value.OrderBy(i => i.Key))
                Console.WriteLine($"    {input.Key} = {input.Value}");
        }
        foreach (var share in stack.Shares)
            Console.WriteLine($"  shared port {share.Port}: {string.Join(", ", share.Consumers.Select(c => $"{c.Repo}.{c.Input}"))}");
    }

    static void WriteJson<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Emit a success result honouring <c>--json</c>: the machine payload when asked for,
    /// the human line otherwise. Mutating commands route through here so <c>--json</c> is a promise
    /// scripts can rely on everywhere, not just on the read commands.</summary>
    static int Ok(bool json, string human, object payload)
    {
        if (json) WriteJson(payload);
        else Console.WriteLine(human);
        return 0;
    }

    static string Version()
        => typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    static int Help()
    {
        Console.WriteLine($"""
            sprig {Version()} — worktree + infrastructure isolation

            USAGE:
                sprig <command> [options] [--json]

            COMMANDS:
                open                          Launch the sprig desktop app
                update [--check]              Install a newer release in place (--check only reports)
                create <name> --stack <s> | --repo <path>   Create an isolated workspace
                              [--only a,b | --without c]   Partial workspace: a subset of the
                                                           stack's repos (ports left with no
                                                           consumer aren't provisioned)
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
                stack edit <name> [--repos a,b] [--port p] [--bind repo:input=expr]   Amend a stack
                stack ls | show <name> | rm <name> | export <name> <path> | import <path>
                settings [show]               Show port range and restricted ports
                settings set [--start N] [--end N] [--restrict a,b] [--unrestrict a,b]
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

    /// <summary>Take a list option, accepting both <c>--name a,b</c> and a repeated <c>--name</c>.</summary>
    public static List<string> TakeList(ref string[] args, string name)
        => TakeAll(ref args, name)
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

    public static string? FirstPositional(string[] args)
        => args.FirstOrDefault(a => !a.StartsWith('-'));
}
