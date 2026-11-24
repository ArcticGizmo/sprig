using System.ComponentModel;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Orchestrations;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for commands that name one workspace of a repo or an orchestration.</summary>
public class WorkspaceSettings : GlobalSettings
{
    [CommandArgument(0, "[id]")]
    [Description("The repo or orchestration ID (default: the repo this folder is, or the workspace whose worktree it's in)")]
    public string Orchestration { get; set; } = "";

    [CommandArgument(1, "[workspace]")]
    [Description("The workspace: its branch, slot number or folder name (default: the slot this folder is in, else main, the repo where it's checked out)")]
    public string Workspace { get; set; } = "";

    /// <summary>Whether words after the workspace name apps, so one that isn't an ID or a workspace is an app.</summary>
    protected virtual bool TakesApps => false;

    /// <summary>
    /// Fills in what was left out: the ID from the current folder, and the workspace from the worktree it's in, else
    /// <c>main</c>. Outside every repo, a first word that's the name of just one workspace means that workspace. Words
    /// that turn out not to be an ID or one of its workspaces are handed back, for commands whose next argument is an
    /// app.
    /// </summary>
    public IReadOnlyList<string> Resolve(CliContext cli)
    {
        var words = new[] { Orchestration, Workspace }.Where(w => !string.IsNullOrEmpty(w)).ToList();
        string? workspace = null;
        if (words.Count > 0 && cli.Orchestrations.Resolve(words[0]) is not null && !File.Exists(words[0]))
        {
            Orchestration = words[0];
            words.RemoveAt(0);
        }
        else if (Here.TryFind(cli) is { } here) (Orchestration, workspace) = here;
        else if (words.Count > 0 && Here.Named(cli, words[0]) is { } named)
        {
            (Orchestration, Workspace) = (named.Orchestration, named.Name);
            words.RemoveAt(0);
            return words;
        }
        else throw Here.NotHere();

        if (words.Count > 0 && cli.Workspaces.Find(Orchestration, words[0]) is { } found)
        {
            workspace = found.Name;
            words.RemoveAt(0);
        }
        else if (words.Count > 0 && !TakesApps)
        {
            workspace = words[0];
            words.RemoveAt(0);
        }
        Workspace = workspace ?? WorkspaceRecord.MainName;
        return words;
    }
}

/// <summary>What checkout and up both take: the repo or orchestration, and values to keep on the workspace.</summary>
public class ReadySettings : TrustingSettings
{
    [CommandArgument(0, "[id]")]
    [Description("A repo ID from the Repos list, an orchestration ID, or an orchestration file (default: the repo this folder is)")]
    public string? Orchestration { get; set; }

    [CommandOption("--set <name=value>")]
    [Description("Give an input this value in the workspace, over the orchestration's and the repo's default. Kept for next time. Repeat for more")]
    public string[] Set { get; set; } = [];
}

[Description("Check a branch out in a slot of worktrees and make it ready to run: its ports, files and infra's definitions. Nothing starts; run it with sprig up")]
public sealed class CheckoutCommand(CliContext cli) : Command<CheckoutCommand.Settings>
{
    public sealed class Settings : ReadySettings
    {
        [CommandOption("-b|--branch <branch>")]
        [Description("The branch to check out: in the slot it's in already, else the lowest free one, else a new one. Made from --from when it doesn't exist yet")]
        public string? Branch { get; set; }

        [CommandOption("--slot <number>")]
        [Description("The slot to check the branch out in, e.g. 2 for storefront--2")]
        public int? Slot { get; set; }

        [CommandOption("--from <branch>")]
        [Description("Where a branch that doesn't exist yet starts (default: each repo's own default branch)")]
        public string? From { get; set; }

        [CommandOption("--empty-data")]
        [Description("Reusing a free slot, give its infra empty data rather than the data the last branch left")]
        public bool EmptyData { get; set; }

        [CommandOption("--fix-slot")]
        [Description("When the slot is broken (folders in its way that aren't worktrees), delete them and make the slot again, rather than be asked")]
        public bool FixSlot { get; set; }

        [CommandOption("--unset <name>")]
        [Description("Reusing a free slot, clear a value it kept so the default applies again. Repeat for more")]
        public string[] Unset { get; set; } = [];
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.Branch)) return ValidationResult.Error("name the branch to check out: sprig checkout -b <branch>");
        if (SettingValues.Parse(s.Set, out _) is { } error) return ValidationResult.Error(error);
        return ValidationResult.Success();
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var branch = s.Branch!;
        var id = s.Orchestration ?? Here.Find(cli, branch).Id;
        if (OrchestrationLoad.Load(cli, id, s) is not { } loaded) return 1;

        var readying = new Readying(cli, s.Json);
        try
        {
            var (slot, fixSlot) = AskAboutBrokenSlot(loaded, branch, s);
            var record = readying.Approving(loaded, s, takeOver: false, (values, _) =>
                cli.Workspaces.CheckOut(loaded, branch, new CheckOutOptions(slot, s.From, s.EmptyData, fixSlot), readying.Progress, cancellation,
                    values: values, unset: s.Unset));
            if (record is null) return 1;
            var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;
            if (s.Json) CliOutput.Json(new { ok = true, workspace = Shown.Record(record, plan, s), plan = Shown.Plan(plan, s), secretsUnmasked = s.UnmaskSecrets });
            else readying.Summary(record, plan, appsStart: false);
            return 0;
        }
        catch (WorkspaceException e)
        {
            return readying.Failed(e, loaded);
        }
    }

    /// <summary>
    /// The slot to use and whether to fix it, asking when the one the branch would go in is broken. <c>--fix-slot</c>
    /// answers without asking; without a terminal to ask in, it's refused, naming both ways on.
    /// </summary>
    (int? Slot, bool Fix) AskAboutBrokenSlot(LoadedOrchestration loaded, string branch, Settings s)
    {
        if (s.FixSlot || cli.Workspaces.BrokenSlotFor(loaded, branch, s.Slot) is not { } broken) return (s.Slot, s.FixSlot);
        if (!cli.Interactive(s.Json))
            throw new WorkspaceException($"{broken.Why}. Fix it with --fix-slot (deleting what's in the way), or use --slot {broken.Next} for {broken.NextName}") { Code = CliErrors.SlotBroken };
        cli.Ansi.MarkupLine($"[yellow]{Markup.Escape(broken.Why)}.[/]");
        var next = $"Use {broken.NextName} instead";
        var fix = $"Fix {broken.SlotName}: delete what's in the way and make it again";
        var answer = cli.Ansi.Prompt(new SelectionPrompt<string>().Title("What now?").AddChoices(next, fix));
        return answer == fix ? (s.Slot, true) : (int.Parse(broken.Next, System.Globalization.CultureInfo.InvariantCulture), false);
    }
}

[Description("Run a workspace that's checked out: its apps in the background, after the infra they use. With none named, the slot this folder is in, else main (the repos where they're checked out). Check a branch out in a slot first with sprig checkout")]
public sealed class UpCommand(CliContext cli) : Command<UpCommand.Settings>
{
    public sealed class Settings : ReadySettings
    {
        [CommandArgument(1, "[workspace]")]
        [Description("The workspace: its branch, slot number or folder name (default: the slot this folder is in, else main)")]
        public string? Workspace { get; set; }

        [CommandOption("--take-over")]
        [Description("When another definition's main is running in the same checkout, stop it (keeping its data) and start this one there, rather than be asked")]
        public bool TakeOver { get; set; }

        [CommandOption("--group <name>")]
        [Description("Start only this group of apps (and what they depend on), rather than every app")]
        public string? Group { get; set; }

        [CommandOption("--run")]
        [Description("Follow the apps' logs together here, rather than leaving them in the background. Ctrl+C stops everything it started, keeping the infra's data")]
        public bool Run { get; set; }

        [CommandOption("--no-wait")]
        [Description("Return once the infra is up and the apps are starting, without waiting for them to be ready (sprig wait does that later)")]
        public bool NoWait { get; set; }

        // Kept out of help so that an agent or a habit reaching for them is told where checking out went.
        [CommandOption("-b|--branch <branch>", IsHidden = true)]
        public string? Branch { get; set; }

        [CommandOption("--no-start", IsHidden = true)]
        public bool NoStart { get; set; }
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
    {
        if (s.Branch is { } branch)
            return ValidationResult.Error($"up runs a workspace that's checked out already. Check {branch} out with: sprig checkout -b {branch}, then run it with: sprig up {branch}");
        if (s.NoStart) return ValidationResult.Error("up always starts. To make a branch ready without starting anything, use: sprig checkout -b <branch>");
        if (SettingValues.Parse(s.Set, out _) is { } error) return ValidationResult.Error(error);
        return ValidationResult.Success();
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.Json && s.Run)
            throw new InvalidOperationException("--run follows the apps' logs, which isn't JSON; leave it off with --json and the apps run in the background");
        if (s.Run && s.NoWait) throw new InvalidOperationException("--run follows the apps until Ctrl+C, so there's nothing for --no-wait to skip; use one or the other");
        var (id, workspace) = Target(s);
        if (OrchestrationLoad.Load(cli, id, s, workspace) is not { } loaded) return 1;

        var readying = new Readying(cli, s.Json);
        try
        {
            var record = readying.Approving(loaded, s, s.TakeOver, (values, takeOver) => Up(loaded, workspace, values, takeOver, readying.Progress, s, cancellation));
            if (record is null) return 1;
            var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;
            if (!s.Json) readying.Summary(record, plan, appsStart: true);
            var specs = RunnerSpecs.For(record, plan, Group(plan, s));
            if (specs.Count == 0)
            {
                if (s.Json) CliOutput.Json(new { ok = true, workspace = Shown.Record(record, plan, s), plan = Shown.Plan(plan, s), runners = Array.Empty<RunnerStatus>(), secretsUnmasked = s.UnmaskSecrets });
                else cli.Ansi.MarkupLine("[dim]The orchestration has no apps to run.[/]");
                return 0;
            }
            return s.Run ? new AppFollow(cli, record, specs).Run() : StartDetached(record, plan, specs, s, cancellation);
        }
        catch (WorkspaceException e)
        {
            return readying.Failed(e, loaded);
        }
    }

    /// <summary>
    /// The workspace to run: the one named (by ID and workspace, or just the workspace in a repo's folder), else the slot
    /// this folder is in, else main. One that isn't checked out is refused rather than made, since making it is checkout's.
    /// </summary>
    (string Id, string Workspace) Target(Settings s)
    {
        var words = new[] { s.Orchestration, s.Workspace }.OfType<string>().Where(w => w.Length > 0).ToList();
        string id;
        string? here = null;
        var idGiven = false;
        if (words.Count > 0 && cli.Orchestrations.Resolve(words[0]) is not null)
        {
            (id, idGiven) = (words[0], true);
            words.RemoveAt(0);
        }
        else if (Here.TryFind(cli) is { } found) (id, here) = found;
        else if (words.Count > 0 && Here.Named(cli, words[0]) is { } named)
        {
            (id, here) = (named.Orchestration, named.Name);
            words.RemoveAt(0);
        }
        else throw Here.NotHere();
        if (words.Count > 1) throw new InvalidOperationException($"no orchestration or repo named '{words[0]}'");

        var word = words.Count == 1 ? words[0] : here ?? WorkspaceRecord.MainName;
        if (word == WorkspaceRecord.MainName) return (id, word);
        var record = cli.Workspaces.Find(id, word)
            ?? throw new InvalidOperationException($"{id} has no workspace '{word}'. Check it out first with: sprig checkout {(idGiven ? $"{id} " : "")}-b {word}");
        if (record.IsFree)
            throw new InvalidOperationException($"{record.SlotName} is free. Check a branch out in it first with: sprig checkout {id} -b <branch> --slot {record.Name}");
        return (id, record.Name);
    }

    WorkspaceRecord Up(LoadedOrchestration loaded, string workspace, IReadOnlyDictionary<string, string> values, bool takeOver,
        IProgress<WorkspaceEvent> progress, Settings s, CancellationToken cancellation)
    {
        string? held = null;
        try
        {
            return cli.Workspaces.Up(loaded, workspace, null, null, progress, cancellation, (record, plan) => held = Prebuild(record, plan, s),
                values, takeOver, new Starting(s.Group));
        }
        catch (Exception) when (held is not null)
        {
            cli.Host.TrySend(new HostRequest("stop", Workspace: held));
            throw;
        }
    }

    /// <summary>The apps <c>--group</c> picks; null for every app.</summary>
    static IReadOnlyList<string>? Group(Plan plan, Settings s) => s.Group is { } group ? RunnerSpecs.Group(plan, group) : null;

    string? Prebuild(WorkspaceRecord record, Plan plan, Settings s) => cli.Host.Prebuild(record, plan, Group(plan, s));

    int StartDetached(WorkspaceRecord record, Plan plan, IReadOnlyList<RunnerSpec> specs, Settings s, CancellationToken cancellation)
    {
        var json = s.Json;
        cli.Ansi.WriteLine();
        StartCommand.Send(cli, specs, []);
        if (s.NoWait)
        {
            var words = Words(record);
            return CliOutput.Ok(json, $"starting {specs.Count} apps in the background; wait until they're ready with: sprig wait {words}",
                new { ok = true, workspace = Shown.Record(record, plan, s), plan = Shown.Plan(plan, s), apps = specs.Select(x => x.Name), secretsUnmasked = s.UnmaskSecrets });
        }
        var runners = StartCommand.WaitUntilReady(cli, record.Key, specs, json, cancellation);
        var failure = StartFailure.Of(plan, runners);
        var ok = failure is null;
        if (json)
            CliOutput.Json(new
            {
                ok, code = failure?.Code, error = failure?.Error,
                workspace = Shown.Record(record, plan, s), plan = Shown.Plan(plan, s), runners, failed = failure?.Apps, secretsUnmasked = s.UnmaskSecrets,
            });
        else
        {
            var names = Markup.Escape(Words(record));
            cli.Ansi.MarkupLine($"[dim]The apps keep running in the background. Watch them: sprig ps; read one's log: sprig logs {names} <app>; " +
                                $"stop them: sprig stop {names}[/]");
        }
        return ok ? 0 : 1;
    }

    /// <summary>How to name the workspace on a command line: its ID, then its branch (or its name, in place).</summary>
    internal static string Words(WorkspaceRecord record) => $"{record.Orchestration} {record.Branch ?? record.Name}";

    /// <summary>The <c>sprig up</c> that brings this workspace up again.</summary>
    internal static string UpLine(WorkspaceRecord record)
        => record.InPlace ? $"sprig up {record.Orchestration}" : $"sprig up {Words(record)}";
}

/// <summary>What checkout and up share: making a workspace ready, asking for what that needs, and saying how it went.</summary>
sealed class Readying(CliContext cli, bool json)
{
    SyncProgress<WorkspaceEvent>? _progress;
    string? _lastOutput;

    public IProgress<WorkspaceEvent> Progress => _progress ??= new SyncProgress<WorkspaceEvent>(Report);

    /// <summary>
    /// <paramref name="make"/>, given the values so far and whether to take the checkout over, asking to approve the
    /// definitions a workspace's worktrees bring (its branch can change them), for values nothing gives, and to take over
    /// a checkout another definition's main is running in, then going again. Null when one of those was turned down.
    /// </summary>
    public WorkspaceRecord? Approving(LoadedOrchestration loaded, ReadySettings s, bool takeOver,
        Func<IReadOnlyDictionary<string, string>, bool, WorkspaceRecord> make)
    {
        SettingValues.Parse(s.Set, out var given);
        var values = new Dictionary<string, string>(given, StringComparer.Ordinal);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return make(values, takeOver);
            }
            catch (WorkspaceException e) when (e.Untrusted.Count > 0 && attempt < 3)
            {
                if (!TrustFlow.Approve(cli, e.Untrusted, s)) return null;
            }
            catch (WorkspaceException e) when (e.HeldBy is { } holder && !takeOver)
            {
                if (!cli.Interactive(json))
                    throw new WorkspaceException($"{e.Message}, with --take-over") { HeldBy = holder };
                cli.Ansi.MarkupLine($"[yellow]{Markup.Escape(holder.SlotName)} is running in the same checkout.[/]");
                if (!cli.Ansi.Prompt(new ConfirmationPrompt($"Stop {Markup.Escape(holder.SlotName)} (keeping its data) and start {Markup.Escape(loaded.Id)} there?") { DefaultValue = false }))
                    return null;
                takeOver = true;
            }
            catch (WorkspaceException e) when (e.Asks.Count > 0 && attempt < 3 && cli.Interactive(json))
            {
                cli.Ansi.MarkupLine($"[yellow]{(e.Asks.Count == 1 ? "An input has" : "Some inputs have")} no value yet.[/] It's kept on the workspace.");
                foreach (var ask in e.Asks.Where(a => !values.ContainsKey(a.Name)))
                {
                    var prompt = new TextPrompt<string>($"  [bold]{Markup.Escape(ask.Name)}[/] [dim]({Markup.Escape(ask.Type)}, for {Markup.Escape(ask.Owner)})[/]:");
                    if (ask.Type == "secret") prompt = prompt.Secret();
                    values[ask.Name] = cli.Ansi.Prompt(prompt);
                }
            }
        }
    }

    public int Failed(WorkspaceException e, LoadedOrchestration loaded)
    {
        if (json) CliOutput.Json(CliErrors.Payload(e));
        else
        {
            Console.Error.WriteLine($"error: {e.Message}");
            if (e.Problems.Count > 0) PlanPrinter.Problems(cli.Ansi, e.Problems, Path.GetDirectoryName(loaded.OrchestrationPath)!);
            if (e.Asks.Count > 0)
                cli.Ansi.MarkupLine($"[dim]Give {(e.Asks.Count == 1 ? "it" : "them")} a value with {Markup.Escape(string.Join(" ", e.Asks.Select(a => $"--set {a.Name}=...")))}[/]");
        }
        return 1;
    }

    void Report(WorkspaceEvent e)
    {
        if (json) return;
        // Compose prints some progress lines twice.
        if (e.State == StepState.Output && e.Detail == _lastOutput) return;
        _lastOutput = e.State == StepState.Output ? e.Detail : null;
        switch (e.State)
        {
            case StepState.Running:
                cli.Ansi.MarkupLine($"[blue]…[/] {Markup.Escape(e.Step)}");
                break;
            case StepState.Done:
                cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(e.Step)}{(e.Detail is { } d ? $"  [dim]{Markup.Escape(d)}[/]" : "")}");
                break;
            case StepState.Failed:
                cli.Ansi.MarkupLine($"[red]x[/] {Markup.Escape(e.Step)}");
                break;
            case StepState.Output when !string.IsNullOrWhiteSpace(e.Detail):
                cli.Ansi.MarkupLine($"    [dim]{Markup.Escape(e.Detail!.Trim())}[/]");
                break;
        }
    }

    /// <param name="appsStart">Sprig starts the apps next, so there's no need to say how to start them by hand.</param>
    public void Summary(WorkspaceRecord record, Plan plan, bool appsStart)
    {
        var root = Path.GetDirectoryName(record.OrchestrationPath)!;
        cli.Ansi.WriteLine();
        cli.Ansi.MarkupLine(record.IsMain
            ? $"[bold]{Markup.Escape(record.Orchestration)}[/] is ready in its main checkout{(record.CheckoutBranch is { } on ? $", on [bold]{Markup.Escape(on)}[/]" : "")}  [dim](as it is, with your own infra)[/]"
            : record.InPlace
            ? $"[bold]{Markup.Escape(record.Orchestration)}[/] / [bold]{Markup.Escape(record.Name)}[/] is ready  [dim](repos in place)[/]"
            : $"[bold]{Markup.Escape(record.Branch ?? "")}[/] is ready in [bold]{Markup.Escape(record.SlotName)}[/]  [dim]({Markup.Escape((record.Folder ?? "").Replace('\\', '/'))})[/]");

        var apps = new Table().Border(TableBorder.Rounded).AddColumns("App", "Folder", "Url", "Values in");
        foreach (var app in plan.Apps)
        {
            var repo = record.Repos.Single(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var url = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name) is { Port: { } port } ? $"http://localhost:{port}" : "";
            var files = app.ValueFiles();
            apps.AddRow(Markup.Escape($"{repo.Name}/{app.Name}"), Markup.Escape(dir.Replace('\\', '/')), Markup.Escape(url),
                files.Count > 0 ? Markup.Escape(string.Join(", ", files)) : "[dim]none[/]");
        }
        cli.Ansi.Write(apps);

        if (plan.Containers.Count + plan.Compose.Count > 0)
        {
            var infra = new Table().Border(TableBorder.Rounded).AddColumns("Infra", "Ports");
            if (record.IsMain)
                foreach (var own in cli.Workspaces.OwnInfraOf(plan))
                    infra.AddRow(Markup.Escape(own.Key), $"yours, on {Markup.Escape(string.Join(", ", own.Ports))}");
            else
            {
                foreach (var c in plan.Containers) infra.AddRow(Markup.Escape($"{c.Owner}/{c.Name}"), Markup.Escape(string.Join(", ", c.Ports)));
                foreach (var c in plan.Compose)
                    foreach (var svc in c.Services)
                        infra.AddRow(Markup.Escape($"{c.Owner}/{svc.Name}"), Markup.Escape(string.Join(", ", svc.Mappings)));
            }
            cli.Ansi.Write(infra);
        }

        var warnings = plan.Problems.Where(p => p.Severity == ProblemSeverity.Warning).ToArray();
        if (warnings.Length > 0) PlanPrinter.Problems(cli.Ansi, warnings, root);
        var names = Markup.Escape(UpCommand.Words(record));
        if (!appsStart)
            cli.Ansi.MarkupLine($"[dim]Nothing is running yet. Run it with: sprig up {names}, or start part of it with: sprig start {names} <app>[/]");
    }
}

[Description("Free a slot for another branch: stop its apps and infra and detach its worktrees, keeping its branch, data, ports and values")]
public sealed class FreeCommand(CliContext cli) : Command<FreeCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--agent-made")]
        [Description("Free every slot an agent's worktree hook made that's still taken (only the ID's, when one is named), rather than one workspace. Slots with something running are left, unless --running")]
        public bool AgentMade { get; set; }

        [CommandOption("--running")]
        [Description("With --agent-made, free slots with apps or infra running too, stopping them")]
        public bool Running { get; set; }
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
    {
        if (s.AgentMade && !string.IsNullOrEmpty(s.Workspace)) return ValidationResult.Error("--agent-made frees every slot an agent made; name an ID at most, not a workspace");
        if (s.Running && !s.AgentMade) return ValidationResult.Error("--running goes with --agent-made");
        return ValidationResult.Success();
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.AgentMade) return FreeAgentMade(s, cancellation);
        s.Resolve(cli);
        var before = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace '{s.Workspace}'");
        var record = cli.Workspaces.Free(s.Orchestration, s.Workspace, new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        if (s.Json) { CliOutput.Json(new { ok = true, workspace = Shown.Record(record, cli.Workspaces.LastPlan(record.Orchestration, record.Name), s), secretsUnmasked = s.UnmaskSecrets }); return 0; }
        CliOutput.Success(before.IsFree ? $"{record.SlotName} was already free" : $"{record.SlotName} is free; '{before.Branch}' is kept, with its commits");
        cli.Ansi.MarkupLine($"[dim]Check the next branch out in it with: sprig checkout {Markup.Escape(record.Orchestration)} -b <branch> --slot {Markup.Escape(record.Name)}[/]");
        return 0;
    }

    /// <summary>
    /// Agents' subagent and workflow worktrees never fire Claude Code's remove hook, so the slots made for them stay taken
    /// until something frees them. Whether an agent is done with one can't be told from here, so one with something
    /// running is taken to be in use.
    /// </summary>
    int FreeAgentMade(Settings s, CancellationToken cancellation)
    {
        var id = string.IsNullOrEmpty(s.Orchestration) ? null
            : cli.Orchestrations.Resolve(s.Orchestration) is not null ? s.Orchestration
            : throw new InvalidOperationException($"no repo or orchestration named '{s.Orchestration}'");
        var made = cli.Workspaces.List().Where(r => r.MadeForAgent is not null && !r.IsFree && !r.IsMain && (id is null || r.Orchestration == id)).ToArray();
        var runners = made.Length == 0 ? [] : cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [];
        var projects = made.Length == 0 ? null : cli.Workspaces.RunningProjects();

        var freed = new List<string>();
        var left = new List<string>();
        foreach (var record in made)
        {
            if (!s.Running && WorkspaceService.RunState(record, runners, projects) is "running" or "starting")
            {
                left.Add(record.SlotName);
                continue;
            }
            cli.Workspaces.Free(record.Orchestration, record.Name, new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
            freed.Add(record.SlotName);
        }

        if (s.Json) { CliOutput.Json(new { ok = true, freed, running = left }); return 0; }
        if (made.Length == 0) cli.Ansi.MarkupLine("[dim]No slot an agent made is taken.[/]");
        foreach (var slot in freed) CliOutput.Success($"{slot} is free");
        if (left.Count > 0)
            cli.Ansi.MarkupLine($"[yellow]Left running:[/] {Markup.Escape(string.Join(", ", left))} [dim](add --running to free them too)[/]");
        return 0;
    }
}

[Description("Stop everything a workspace runs, its apps and its infra, keeping its containers, data, worktrees and ports (the same as sprig stop with nothing named)")]
public sealed class DownCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        return StopAll(cli, s, cancellation);
    }

    internal static int StopAll(CliContext cli, WorkspaceSettings s, CancellationToken cancellation)
    {
        var record = cli.Workspaces.Down(s.Orchestration, s.Workspace, new SyncProgress<WorkspaceEvent>(e => Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, record.IsFree ? $"{record.SlotName} is free, so nothing was running"
                : record.IsMain ? $"{record.SlotName} has stopped; your infra is left running"
                : $"{record.SlotName} ({record.Label}) has stopped, its apps and its infra",
            new { ok = true, workspace = record });
    }

    internal static void Echo(CliContext cli, WorkspaceEvent e, bool json)
    {
        if (json) return;
        if (e.State == StepState.Done) cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(e.Step)}{(e.Detail is { } d ? $"  [dim]{Markup.Escape(d)}[/]" : "")}");
        else if (e.State == StepState.Failed) cli.Ansi.MarkupLine($"[red]x[/] {Markup.Escape(e.Step)}");
    }
}

[Description("Empty a workspace's data: delete its containers' volumes, then start again the apps that were running, and the infra they use")]
public sealed class EmptyDataCommand(CliContext cli) : Command<EmptyDataCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("-y|--yes")]
        [Description("Don't ask first")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace)
                     ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace '{s.Workspace}' yet, so it has no data to empty");
        if (!s.Yes)
        {
            if (!cli.Interactive(s.Json)) throw new InvalidOperationException("emptying a workspace's data deletes its databases; pass --yes to confirm");
            if (!cli.Ansi.Prompt(new ConfirmationPrompt($"Empty {Markup.Escape(record.SlotName)}'s data: delete its containers' volumes, databases included?") { DefaultValue = false }))
                return 1;
        }

        // Taken before, since emptying stops them.
        var running = (cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [])
            .Where(r => r.Workspace == record.Key && r.State.IsAlive()).Select(r => r.Name).ToArray();
        var wasUp = record.State == WorkspaceState.Up;
        var progress = new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json));
        record = cli.Workspaces.EmptyData(s.Orchestration, s.Workspace, progress, cancellation);
        if (!wasUp)
            return CliOutput.Ok(s.Json, $"{record.SlotName}'s data is empty; it starts that way next time", new { ok = true, workspace = record, restarted = Array.Empty<string>() });

        var again = $"start it again with: {UpCommand.UpLine(record)}";
        if (OrchestrationLoad.Load(cli, record.Orchestration, new TrustingSettings { Json = s.Json, UnmaskSecrets = s.UnmaskSecrets }, record.Name) is not { } loaded)
        {
            Console.Error.WriteLine($"{record.SlotName}'s data is empty, but it can't come back up yet; {again}");
            return 1;
        }
        string[] apps = [];
        string? held = null;
        try
        {
            // What was running comes back, with the infra it uses; the rest stays stopped.
            record = cli.Workspaces.Up(loaded, record.Name, null, progress: progress, cancellation: cancellation, beforeInfra: (r, plan) =>
            {
                apps = running.Where(n => plan.Apps.Any(a => a.Key == n && !a.Job)).ToArray();
                // Their jobs run again: a migration that finished did so against the data that's gone.
                if (apps.Length > 0) held = cli.Host.Prebuild(r, plan, apps, jobsAgain: true);
            }, starting: new Starting(Apps: running));
        }
        catch (Exception e) when (e is WorkspaceException or OperationCanceledException)
        {
            if (held is not null) cli.Host.TrySend(new HostRequest("stop", Workspace: held));
            if (e is OperationCanceledException) throw;
            Console.Error.WriteLine($"error: {record.SlotName}'s data is empty, but it didn't start again: {e.Message}; {again}");
            return 1;
        }

        var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;
        var specs = apps.Length == 0 ? [] : RunnerSpecs.For(record, plan, apps);
        if (specs.Count == 0)
            return CliOutput.Ok(s.Json, $"{record.SlotName}'s data is empty; nothing was running, so nothing was started", new { ok = true, workspace = record, restarted = Array.Empty<string>() });

        // Releases the held builds; naming them to restart would build them again.
        StartCommand.Send(cli, specs, held is null ? RunnerSpecs.Jobs(specs) : []);
        var runners = StartCommand.WaitUntilReady(cli, record.Key, specs, s.Json, cancellation);
        var ok = runners.All(r => r.State.Satisfies());
        if (s.Json) CliOutput.Json(new { ok, workspace = record, restarted = apps, runners });
        else if (ok) CliOutput.Success($"{record.SlotName}'s data is empty, and its infra and {(apps.Length == 1 ? "the app that was running are" : $"the {apps.Length} apps that were running are")} back");
        return ok ? 0 : 1;
    }
}

[Description("Remove a workspace: its containers and their data, its worktrees, and the values it wrote")]
public sealed class RmCommand(CliContext cli) : Command<RmCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--delete-branch")]
        [Description("Also delete the workspace's branch in each repo")]
        public bool DeleteBranch { get; set; }

        [CommandOption("--force")]
        [Description("Remove worktrees with uncommitted changes, and delete branches with unpushed commits")]
        public bool Force { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Don't ask first")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'");
        if (!s.Yes)
        {
            if (!cli.Interactive(s.Json)) throw new InvalidOperationException("removing a workspace deletes its databases; pass --yes to confirm");
            var what = record.InPlace ? "its containers and their data, and the values it wrote into the repos"
                : $"its containers and their data, and its worktrees{(s.DeleteBranch && record.Branch is { } b ? $" and the branch '{b}'" : "")}";
            if (!cli.Ansi.Prompt(new ConfirmationPrompt($"Remove {Markup.Escape(record.SlotName)}: {Markup.Escape(what)}?") { DefaultValue = false }))
                return 1;
        }
        cli.Workspaces.Remove(s.Orchestration, s.Workspace, new RemoveOptions(s.DeleteBranch, s.Force),
            new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, $"removed {record.SlotName}", new { ok = true, orchestration = s.Orchestration, workspace = s.Workspace });
    }
}

[Description("List workspaces")]
public sealed class WorkspacesCommand(CliContext cli) : Command<WorkspacesCommand.Settings>
{
    /// <summary>Whether it's running, and while it is, whether its apps are up, in colour (see
    /// <see cref="WorkspaceService.RunState"/> and <see cref="AppsReadiness"/>).</summary>
    internal static string RunState(WorkspaceRecord r, Plan? plan, IReadOnlyList<RunnerStatus> runners, IReadOnlySet<string>? projects)
    {
        var state = WorkspaceService.RunState(r, runners, projects);
        var readiness = AppsReadiness.Of(r, plan, runners);
        var detail = readiness.Detail(state) is { Length: > 0 } d ? $" [dim]({Markup.Escape(d)})[/]" : "";
        return state switch
        {
            "running" => readiness.Health switch
            {
                AppsHealth.Starting => "[blue]starting[/]",
                AppsHealth.Trouble => "[red]running[/]",
                _ => "[green]running[/]",
            } + detail,
            "failed" => $"[red]failed[/] [dim]{Markup.Escape(r.Error ?? "")}[/]",
            "starting" => "[yellow]starting[/]",
            "paused" => "[yellow]paused[/] [dim](sprig resume)[/]",
            var other => $"[dim]{other}[/]",
        };
    }

    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Only the workspaces of this orchestration or repo ID")]
        public string? Orchestration { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspaces = cli.Workspaces.List().Where(r => s.Orchestration is null || r.Orchestration.Equals(s.Orchestration, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (s.Json) { CliOutput.Json(workspaces.Select(r => Shown.Record(r, cli.Workspaces.LastPlan(r.Orchestration, r.Name), s))); return 0; }
        if (workspaces.Length == 0)
        {
            cli.Ansi.MarkupLine("[dim]No workspaces. In a repo's folder, check a branch out in a slot of its own with: sprig checkout -b <branch>, then run it with: sprig up <branch> (or run the checkout where it is with: sprig up)[/]");
            return 0;
        }
        var runners = cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [];
        var projects = cli.Workspaces.RunningProjects();
        string State(WorkspaceRecord r) => RunState(r, cli.Workspaces.LastPlan(r.Orchestration, r.Name), runners, projects);
        // Main checkouts run as they are, with nothing written into them, so they're listed apart from the slots.
        var mains = workspaces.Where(r => r.IsMain).ToArray();
        if (mains.Length > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("Main checkouts [dim](run as they are, with your own infra)[/]")
                .AddColumns("ID", "On", "State", "Folder", "Updated");
            foreach (var r in mains)
                table.AddRow(Markup.Escape(r.Orchestration), r.CheckoutBranch is { } on ? Markup.Escape(on) : "[dim]?[/]", State(r),
                    Folder(r), r.UpdatedAt.LocalDateTime.ToString("g"));
            cli.Ansi.Write(table);
        }
        var others = workspaces.Where(r => !r.IsMain).ToArray();
        if (others.Length > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("Workspaces").AddColumns("ID", "Slot", "Branch", "State", "Folder", "Updated");
            foreach (var r in others)
                table.AddRow(Markup.Escape(r.Orchestration), Markup.Escape(r.SlotName) + Links(r),
                    r.InPlace ? "[dim](in place)[/]" : r.Branch is { } b ? Markup.Escape(b) : "[dim](detached)[/]", State(r),
                    Folder(r), r.UpdatedAt.LocalDateTime.ToString("g"));
            cli.Ansi.Write(table);
        }
        return 0;

        static string Folder(WorkspaceRecord r) => Markup.Escape((r.Folder ?? string.Join(", ", r.Repos.Select(x => x.Dir))).Replace('\\', '/'));

        // A link to a workspace that's gone is shown in red, since making this one ready fails until it's sorted.
        string Links(WorkspaceRecord r) => r.Links is not { Count: > 0 } links ? ""
            : " [dim]🔗[/] " + string.Join(", ", links.Select(l =>
                l.Workspace != WorkspaceRecord.MainName && cli.Workspaces.Get(l.Orchestration, l.Workspace) is null
                    ? $"[red]{Markup.Escape(cli.Workspaces.LinkLabel(l))} (gone)[/]"
                    : $"[dim]{Markup.Escape(cli.Workspaces.LinkLabel(l))}[/]"));
    }
}

/// <summary>Calls back on the reporting thread, so progress prints in order. A tool's stdout and stderr arrive on
/// different threads, hence the lock.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate) report(value);
    }
}
