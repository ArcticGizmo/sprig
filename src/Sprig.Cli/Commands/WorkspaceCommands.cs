using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Docker;
using Sprig.Core.Init;
using Sprig.Core.Stacks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// The workspace verbs and the meta commands (open/update/init). The workspace is the primary object,
// so these verbs stay top-level and unqualified; `ws`/`workspace` mirrors them as an optional prefix
// (wired in CliApp). Each command takes the shared CliContext and preserves the exact --json contract
// and human wording the hand-rolled dispatcher shipped.

[Description("Create an isolated workspace from a stack or a single repo")]
public sealed class CreateCommand(CliContext cli) : Command<CreateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; set; } = "";

        [CommandOption("--stack <name>")]
        [Description("Create from a named stack")]
        public string? Stack { get; set; }

        [CommandOption("--repo <path>")]
        [Description("Create from a single repo path")]
        public string? Repo { get; set; }

        [CommandOption("--only <repos>")]
        [Description("Partial: only these stack repos (comma-separated or repeated)")]
        public string[] Only { get; set; } = [];

        [CommandOption("--without <repos>")]
        [Description("Partial: every stack repo except these")]
        public string[] Without { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var only = CliFormat.SplitList(s.Only);
        var without = CliFormat.SplitList(s.Without);

        if ((only.Count > 0 || without.Count > 0) && s.Stack is null)
            throw new ArgumentException("--only/--without narrow a stack — they need --stack <name>");

        var record = s.Stack is not null
                ? cli.Workspaces.Create(cli.Resolver.Resolve(s.Stack, Selection(cli.Stacks, s.Stack, only, without)), s.Name)
            : s.Repo is not null ? cli.Workspaces.Create(s.Repo, s.Name)
            : throw new ArgumentException("create requires --stack <name> or --repo <path>");

        if (s.Json) { CliOutput.Json(record); return 0; }
        Console.WriteLine($"created workspace '{record.Workspace}'{(record.Stack is { } st ? $" from stack '{st}'" : "")}");
        if (record.IsPartial)
            Console.WriteLine($"  partial: without {string.Join(", ", record.ExcludedRepos)}" +
                (record.SkippedPorts.Count > 0 ? $"; ports not provisioned: {string.Join(", ", record.SkippedPorts)}" : ""));
        if (record.Ports.Count > 0)
            Console.WriteLine($"  ports: {CliFormat.Ports(record.Ports)}");
        foreach (var r in record.Repos)
        {
            Console.WriteLine($"  {r.Name}: {r.WorktreePath}  [{r.Branch}]");
            if (r.Inputs.Count > 0)
                Console.WriteLine($"    inputs: {CliFormat.Kv(r.Inputs)}");
            // Group setup by module; only show module headers when a repo has more than one, so a
            // single-module (or legacy) repo prints exactly as before.
            var setupByModule = r.Setup.GroupBy(x => x.Module).ToList();
            var showModuleHeaders = setupByModule.Count > 1;
            foreach (var group in setupByModule)
            {
                if (showModuleHeaders && group.Key is { } module)
                    Console.WriteLine($"    module {module}:");
                var indent = showModuleHeaders ? "      " : "    ";
                foreach (var step in group)
                {
                    Console.WriteLine($"{indent}setup {(step.Success ? "✓" : "✗")} {step.Command}{(step.Success ? "" : $" (exit {step.ExitCode})")}");
                    if (!step.Success && !string.IsNullOrWhiteSpace(step.Output))
                        foreach (var line in step.Output.TrimEnd().Split('\n'))
                            Console.WriteLine($"{indent}  {line.TrimEnd()}");
                }
            }
        }
        if (record.Repos.Any(r => r.Setup.Any(step => !step.Success)))
            Console.WriteLine("  note: a setup command failed — the workspace was kept; finish setup manually in the worktree.");
        return 0;
    }

    /// <summary>Turn <c>--only</c>/<c>--without</c> into the repo subset to create (null = the whole
    /// stack). <c>--without</c> is resolved against the stack's repo list so the two flags are
    /// interchangeable ways of saying the same thing; naming an unknown repo is an error either way.</summary>
    static IReadOnlyList<string>? Selection(StackStore stacks, string stackName, List<string> only, List<string> without)
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
}

[Description("List workspaces")]
public sealed class LsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var all = cli.Workspaces.List();
        if (s.Json) { CliOutput.Json(all); return 0; }
        if (all.Count == 0)
        {
            Console.WriteLine("no workspaces yet — create one with: sprig create <name> --repo <path>");
            return 0;
        }

        var table = CliFormat.Table("WORKSPACE", "REPOS", "PORTS", "STATUS");
        foreach (var r in all.OrderBy(r => r.Workspace))
            table.AddRow(
                Markup.Escape(r.Workspace),
                Markup.Escape(string.Join(",", r.Repos.Select(x => x.Name))),
                Markup.Escape(CliFormat.Ports(r.Ports)),
                Markup.Escape($"{r.LastStatus}"));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Everything about one workspace: repos, ports, drift, live containers")]
public sealed class InfoCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var record = cli.Workspaces.Get(s.Workspace) ?? throw new ArgumentException($"unknown workspace '{s.Workspace}'");
        var report = cli.Reconciler.Inspect(s.Workspace);
        // The one-stop view also folds in the live container state that `status` shows. Best-effort:
        // a workspace's record and drift must remain inspectable even when docker isn't running, so a
        // failure here degrades to null rather than taking the whole command down.
        var containers = TryContainers(s.Workspace);

        if (s.Json) { CliOutput.Json(new { record, drift = report, containers }); return 0; }

        Console.WriteLine($"workspace: {record.Workspace}   status: {record.LastStatus}   created: {record.CreatedAt:u}");
        if (record.IsPartial)
        {
            Console.WriteLine($"partial: stack '{record.Stack}' without {string.Join(", ", record.ExcludedRepos)}");
            if (record.SkippedPorts.Count > 0)
                Console.WriteLine($"  ports not provisioned: {string.Join(", ", record.SkippedPorts)}");
        }
        Console.WriteLine($"ports: {CliFormat.Ports(record.Ports)}");
        foreach (var r in record.Repos)
        {
            var state = report?.Repos.FirstOrDefault(x => x.WorktreePath == r.WorktreePath)?.State;
            Console.WriteLine($"  {r.Name}");
            Console.WriteLine($"    worktree: {r.WorktreePath}  [{state}]");
            Console.WriteLine($"    branch:   {r.Branch}");
        }
        Console.WriteLine(containers switch
        {
            null => "containers: (docker unavailable)",
            { Count: 0 } => "containers: none running",
            _ => "containers:",
        });
        if (containers is { Count: > 0 })
            foreach (var c in containers)
                Console.WriteLine($"  {c.Name}  {c.State}");
        return 0;
    }

    // The live container list for a workspace, or null if docker can't be reached.
    IReadOnlyList<ContainerStatus>? TryContainers(string workspace)
    {
        try { return cli.Workspaces.Status(workspace); }
        catch { return null; }
    }
}

[Description("Tear down a workspace (--force also deletes the branch)")]
public sealed class RmCommand(CliContext cli) : Command<RmCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--force")]
        [Description("Also delete the git branch")]
        public bool Force { get; set; }

        [CommandOption("--yes")]
        [Description("Confirm the (irreversible) removal")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (!s.Yes)
        {
            var msg = $"refusing to remove '{s.Workspace}' without --yes";
            if (s.Json) CliOutput.Json(new { ok = false, error = msg });
            else Console.Error.WriteLine(msg);
            return 1;
        }
        cli.Workspaces.Remove(s.Workspace, s.Force);
        return CliOutput.Ok(s.Json, $"removed '{s.Workspace}'{(s.Force ? " (including branch)" : "")}",
            new { ok = true, workspace = s.Workspace, action = "remove", branchDeleted = s.Force });
    }
}

[Description("Bring the workspace's docker infra up")]
public sealed class UpCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        cli.Workspaces.Up(s.Workspace);
        return CliOutput.Ok(s.Json, $"infra up for '{s.Workspace}'", new { ok = true, workspace = s.Workspace, action = "up" });
    }
}

[Description("Stop infra (--volumes also wipes data)")]
public sealed class DownCommand(CliContext cli) : Command<DownCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--volumes")]
        [Description("Also remove docker volumes (wipes data)")]
        public bool Volumes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        cli.Workspaces.Down(s.Workspace, s.Volumes);
        return CliOutput.Ok(s.Json, $"infra down for '{s.Workspace}'{(s.Volumes ? " (volumes removed)" : "")}",
            new { ok = true, workspace = s.Workspace, action = "down", volumesRemoved = s.Volumes });
    }
}

[Description("Restart infra (down then up)")]
public sealed class ResetCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        cli.Workspaces.Reset(s.Workspace);
        return CliOutput.Ok(s.Json, $"infra reset for '{s.Workspace}'", new { ok = true, workspace = s.Workspace, action = "reset" });
    }
}

[Description("Live container status only (a subset of info)")]
public sealed class StatusCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var containers = cli.Workspaces.Status(s.Workspace);
        if (s.Json) { CliOutput.Json(containers); return 0; }
        if (containers.Count == 0) { Console.WriteLine("no containers running"); return 0; }
        var table = CliFormat.Table("CONTAINER", "STATE");
        foreach (var c in containers)
            table.AddRow(Markup.Escape(c.Name), Markup.Escape($"{c.State}"));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Detect (and optionally repair) drift, one workspace or all")]
public sealed class ReconcileCommand(CliContext cli) : Command<ReconcileCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[workspace]")]
        [Description("A single workspace (omit to check all)")]
        public string? Workspace { get; set; }

        [CommandOption("--repair")]
        [Description("Repair any drift found")]
        public bool Repair { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var reports = s.Workspace is null
            ? cli.Reconciler.InspectAll()
            : cli.Reconciler.Inspect(s.Workspace) is { } r ? [r] : throw new ArgumentException($"unknown workspace '{s.Workspace}'");

        // Run repairs first (when asked) so both output paths can report what was done — the JSON
        // path folds them into one object rather than trailing plain text after the blob.
        var repairs = s.Repair
            ? reports.Where(r => r.HasDrift)
                .Select(r => new { workspace = r.Workspace, actions = cli.Reconciler.Repair(r.Workspace) })
                .ToList()
            : null;

        if (s.Json)
        {
            if (repairs is null) CliOutput.Json(reports);
            else CliOutput.Json(new { reports, repairs });
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
}

[Description("Launch the sprig desktop app")]
public sealed class OpenCommand : Command<GlobalSettings>
{
    // Launch the desktop app (sprig-gui) that ships alongside the CLI — the escape hatch to the GUI
    // when you want something more granular than the terminal offers. Detaches (UseShellExecute, no
    // wait) so the terminal is handed straight back, exactly as if you'd double-clicked the app.
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var exe = LocateGui() ?? throw new FileNotFoundException(
            "could not find the sprig app (sprig-gui). Install sprig via the installer so both " +
            "ship together, or build Sprig.App alongside the CLI.");

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Console.WriteLine("opening sprig…");
        return 0;
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
}

[Description("Install a newer release in place (--check only reports)")]
public sealed class UpdateCommand : Command<UpdateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--check")]
        [Description("Only report whether an update is available")]
        public bool Check { get; set; }
    }

    // Install a newer release in place (or, with --check, just report whether one exists). Delegates
    // to CliUpdater, which drives Velopack's check/download/apply against the same feed as the app.
    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation) => CliUpdater.Run(s.Check);
}

[Description("Detect & propose a .sprig.json for a repo")]
public sealed class InitCommand(CliContext cli) : Command<InitCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[repo]")]
        [Description("Repo path (default: current directory)")]
        public string? RepoArg { get; set; }

        [CommandOption("--repo <path>")]
        [Description("Repo path (alternative to the positional)")]
        public string? Repo { get; set; }

        [CommandOption("--print")]
        [Description("Preview the proposal without writing")]
        public bool Print { get; set; }

        [CommandOption("--force")]
        [Description("Overwrite an existing .sprig.json")]
        public bool Force { get; set; }

        [CommandOption("--register")]
        [Description("Register the repo after writing")]
        public bool Register { get; set; }
    }

    static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var repo = s.Repo ?? s.RepoArg ?? Environment.CurrentDirectory;
        var root = Path.GetFullPath(repo);

        if (!Directory.Exists(root))
            throw new ArgumentException($"path does not exist: {root}");

        var proposal = new InitInspector(cli.Git).Inspect(root);
        var text = JsonSerializer.Serialize(proposal.Config, ConfigJsonOptions);

        // --print previews without touching disk — the one read-only path. --json pairs with it to
        // get the proposal as a machine object; without --print, --json reports the write instead.
        if (s.Print)
        {
            if (s.Json) CliOutput.Json(proposal);
            else
            {
                foreach (var note in proposal.Notes)
                    Console.WriteLine($"note: {note}");
                Console.WriteLine(text);
            }
            return 0;
        }

        var target = Path.Combine(root, ".sprig.json");
        if (File.Exists(target) && !s.Force)
        {
            var msg = $".sprig.json already exists at {target} — pass --force to overwrite, or --print to preview";
            if (s.Json) CliOutput.Json(new { ok = false, error = msg });
            else Console.Error.WriteLine(msg);
            return 1;
        }

        File.WriteAllText(target, text + "\n");
        var registered = s.Register ? cli.Repos.Add(root).Name : null;

        if (s.Json)
        {
            CliOutput.Json(new { ok = true, path = target, registered, notes = proposal.Notes });
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
}
