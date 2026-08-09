using System.Text.RegularExpressions;
using System.Threading;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Sprig.Cli.Commands;

/// <summary>Terminal + prompt helpers shared by the interactive workspace commands. The console is
/// bound straight to real stdout because prompts and the live checklist drive the cursor (which throws
/// on a redirected handle); the shared app console is late-bound for tests, so never reads as
/// interactive.</summary>
static class Term
{
    public static IAnsiConsole Create() => Create(Console.Out, Console.IsOutputRedirected);

    /// <summary>A prompt console bound to <b>stderr</b>. Used by <c>path</c>, whose stdout carries only the
    /// resolved path (captured by a shell wrapper): the menus must render somewhere the wrapper doesn't
    /// swallow, and stderr is still the real terminal even when stdout is piped into <c>$p = sprig path …</c>.</summary>
    public static IAnsiConsole CreateError() => Create(Console.Error, Console.IsErrorRedirected);

    static IAnsiConsole Create(TextWriter output, bool redirected)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Detect,
            ColorSystem = ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(output),
        });
        // Redirected: detection assumes 80 cols and wraps long paths; widen it. Interactive: keep the
        // real terminal width so the live checklist and prompts lay out correctly.
        if (redirected) console.Profile.Width = 200;
        return console;
    }

    // ESC cancels a selection prompt and returns these sentinels, which callers read as "go back / cancel".
    // SelectionPrompt/MultiSelectionPrompt support this; TextPrompt does not.
    const string CancelChoice = " __back__";
    static readonly List<string> CancelList = [];

    /// <summary>Single-select; null when ESC is pressed.</summary>
    public static string? SelectOne(IAnsiConsole console, string title, IEnumerable<string> choices)
    {
        var result = console.Prompt(new SelectionPrompt<string>()
            .Title(title)
            .PageSize(12)
            .AddChoices(choices)
            .AddCancelResult(CancelChoice));
        return result == CancelChoice ? null : result;
    }

    /// <summary>Multi-select with <paramref name="preselect"/> already ticked; null when ESC is pressed.</summary>
    public static List<string>? SelectMany(IAnsiConsole console, string title, IEnumerable<string> choices, IEnumerable<string> preselect)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title(title)
            .Required()
            .PageSize(12)
            .InstructionsText("[grey](space toggles, enter accepts, esc goes back)[/]")
            .AddChoices(choices)
            .AddCancelResult(CancelList);
        foreach (var item in preselect) prompt.Select(item);
        var result = console.Prompt(prompt);
        return ReferenceEquals(result, CancelList) ? null : result;
    }
}

/// <summary>Decides whether a command runs its interactive pickers, from the <c>-i</c>/<c>--ni</c> flags
/// and whether a terminal is actually attached. The default is smart: a bare command at a real terminal
/// opens the wizard, while the same command in a script, a pipe, or CI stays non-interactive (and errors if
/// it's missing required input). The flags force it either way — <c>-i</c> for a picker even when detection
/// is unsure, <c>--ni</c> (like <c>--json</c>) to stay non-interactive even at a terminal. One rule, shared
/// by every command that has an interactive mode, so the behaviour never drifts between them.</summary>
static class Interactivity
{
    // stdin is the channel every picker reads from, so a redirected stdin (a pipe, a file, the test harness,
    // CI) means there's no human to prompt — whatever stdout happens to look like.
    public static bool TerminalAttached => !Console.IsInputRedirected;

    /// <summary>Resolve interactive vs not. <paramref name="hasPrimaryInput"/> is whether the caller already
    /// named what to act on (a workspace, a stack/repo) — a fully-specified command runs straight through
    /// even at a terminal. Throws on contradictory flags, or on a forced <c>-i</c> with no terminal to
    /// prompt on.</summary>
    public static bool Resolve(bool interactive, bool noInteractive, bool json, bool hasPrimaryInput)
    {
        if (interactive && noInteractive)
            throw new ArgumentException("-i and --ni are opposites — pass only one");
        if (interactive && json)
            throw new ArgumentException("-i is interactive — it can't be combined with --json");

        if (interactive)
        {
            if (!TerminalAttached)
                throw new ArgumentException("-i needs an interactive terminal (stdin is redirected)");
            return true;
        }
        if (noInteractive || json)
            return false;

        // Auto: a bare command at a real terminal opens the wizard; give it a target, or run it without a
        // terminal (a script, a pipe, CI), and it stays non-interactive so it never blocks on a prompt.
        return TerminalAttached && !hasPrimaryInput;
    }
}

/// <summary>Resolves the single workspace a target verb (up/down/reset/status/info) acts on. Naming it on
/// the command line always wins; omit it and — at a terminal, when not producing <c>--json</c> — you pick
/// from a list, matching the bare-command-opens-a-picker behaviour of create/rm/cd. The picker is the verbs'
/// only interactive step, so omitting the argument is itself the opt-in; there's no separate <c>-i</c>.</summary>
static class WorkspacePrompt
{
    /// <summary>The positional when given; otherwise an interactive pick. Returns null only when the pick is
    /// cancelled (ESC) — callers treat that as "nothing to do" and exit cleanly. Throws when there's no name
    /// and no terminal to ask on (a script/pipe/CI or <c>--json</c>), or when there are no workspaces.</summary>
    public static string? Resolve(CliContext cli, string? workspace, bool json)
    {
        if (workspace is not null) return workspace;
        if (json || !Interactivity.TerminalAttached)
            throw new ArgumentException("a workspace is required (or run it at a terminal to pick one)");

        var all = cli.Workspaces.List();
        if (all.Count == 0)
            throw new ArgumentException("no workspaces yet — create one with 'sprig create'");

        return Term.SelectOne(Term.Create(), "Select a [green]workspace[/]: [grey](esc cancels)[/]",
            all.Select(w => w.Workspace).OrderBy(x => x, StringComparer.Ordinal));
    }
}

/// <summary>Renders a workspace operation's step plan as a live checklist while the work runs — the
/// same shape as the GUI's progress window. Every step shows up front (pending), then ticks over to
/// running → done/warning as the Core reports it, streaming the running step's latest output line
/// beneath it. Used by both create and destroy.</summary>
static class Checklist
{
    /// <summary>Drive <paramref name="work"/> (which reports progress) while rendering
    /// <paramref name="plan"/>. Animates in an interactive terminal; falls back to an append-only log
    /// when stdout is redirected. Rethrows whatever <paramref name="work"/> throws.</summary>
    public static void Run(IAnsiConsole console, IReadOnlyList<WorkspaceStep> plan, Action<IProgress<WorkspaceStepProgress>> work)
    {
        var rows = plan.Select(p => new StepRow(p.Id, p.Label, p.SubStep)).ToList();
        if (console.Profile.Capabilities.Interactive) RunLive(console, rows, work);
        else RunPlain(console, rows, work);
    }

    static void RunLive(IAnsiConsole console, List<StepRow> rows, Action<IProgress<WorkspaceStepProgress>> work)
    {
        var byId = rows.ToDictionary(r => r.Id);
        var glyphs = GlyphsFor(console);
        var frames = glyphs.Spinner;
        var gate = new object();
        Exception? error = null;
        var finished = false;

        console.Live(Render(rows, glyphs, frames[0]))
            .AutoClear(false)
            .Start(ctx =>
            {
                var progress = new SyncProgress<WorkspaceStepProgress>(p =>
                {
                    lock (gate)
                    {
                        if (!byId.TryGetValue(p.StepId, out var row)) return;
                        if (!string.IsNullOrEmpty(p.Output))
                        {
                            var line = Clean(p.Output!);
                            if (line.Length > 0) row.Output = line;
                        }
                        else { row.State = p.State; if (p.Detail is { } d) row.Detail = Clean(d); }
                    }
                });

                // Run the work off-thread so this thread can keep animating the spinner while a slow
                // step (a dependency install) blocks — Live only redraws when we ask it to.
                var task = Task.Run(() =>
                {
                    try { work(progress); }
                    catch (Exception ex) { error = ex; }
                    finally { Volatile.Write(ref finished, true); }
                });

                for (var frame = 0; !Volatile.Read(ref finished); frame++)
                {
                    lock (gate) ctx.UpdateTarget(Render(rows, glyphs, frames[frame % frames.Length]));
                    Thread.Sleep(80);
                }
                task.GetAwaiter().GetResult();
                lock (gate) ctx.UpdateTarget(Render(rows, glyphs, frames[0]));
            });

        if (error is not null) throw error;
    }

    // Redirected/piped fallback (no cursor control): emit one line as each step starts, so the output
    // is still a readable running log rather than a frozen pause.
    static void RunPlain(IAnsiConsole console, List<StepRow> rows, Action<IProgress<WorkspaceStepProgress>> work)
    {
        var byId = rows.ToDictionary(r => r.Id);
        string? lastRepo = null;
        var progress = new SyncProgress<WorkspaceStepProgress>(p =>
        {
            if (!byId.TryGetValue(p.StepId, out var row) || !string.IsNullOrEmpty(p.Output)) return;
            row.State = p.State;
            if (p.State == WorkspaceStepState.Running)
            {
                // Print the repo heading once, when its first step starts, then indent its steps.
                if (row.Repo != lastRepo)
                {
                    lastRepo = row.Repo;
                    if (row.Repo is { } repo) console.MarkupLine($"[bold]{Markup.Escape(repo)}[/]");
                }
                var indent = row.Repo is null ? "" : row.SubStep ? "      " : "   ";
                console.MarkupLine($"{indent}[grey]>[/] {Markup.Escape(row.Label)}");
            }
            else if (p.State is WorkspaceStepState.Warning or WorkspaceStepState.Error && p.Detail is { } d)
                console.MarkupLine($"  [yellow]{Markup.Escape(Clean(d))}[/]");
        });
        work(progress);
    }

    static IRenderable Render(IReadOnlyList<StepRow> rows, Glyphs glyphs, string spinner)
    {
        var lines = new List<IRenderable>();
        string? currentRepo = null;
        foreach (var r in rows)
        {
            if (r.Repo is { } repo)
            {
                // Head each repo's steps with the repo name, marked by the group's aggregate state, and
                // indent the steps beneath it (setup sub-steps one level deeper).
                if (repo != currentRepo)
                {
                    currentRepo = repo;
                    var groupMarker = Marker(Aggregate(rows.Where(x => x.Repo == repo)), glyphs, spinner);
                    lines.Add(new Markup($"{groupMarker} [bold]{Markup.Escape(repo)}[/]"));
                }
                AddStep(lines, r, glyphs, spinner, r.SubStep ? "      " : "   ");
            }
            else
            {
                currentRepo = null;
                AddStep(lines, r, glyphs, spinner, "");
            }
        }
        return new Rows(lines);
    }

    static void AddStep(List<IRenderable> lines, StepRow r, Glyphs glyphs, string spinner, string indent)
    {
        var marker = Marker(r.State, glyphs, spinner);
        var label = Markup.Escape(r.Label);
        var body = r.State == WorkspaceStepState.Pending ? $"[grey]{label}[/]" : label;
        var detail = string.IsNullOrWhiteSpace(r.Detail) ? "" : $" [dim]{Markup.Escape(r.Detail!)}[/]";
        lines.Add(new Markup($"{indent}{marker} {body}{detail}"));
        if (!string.IsNullOrEmpty(r.Output) &&
            r.State is WorkspaceStepState.Running or WorkspaceStepState.Warning or WorkspaceStepState.Error)
            lines.Add(new Markup($"{indent}    [dim]{Markup.Escape(Truncate(r.Output!))}[/]"));
    }

    static string Marker(WorkspaceStepState state, Glyphs glyphs, string spinner) => state switch
    {
        WorkspaceStepState.Done => glyphs.Done,
        WorkspaceStepState.Warning => glyphs.Warn,
        WorkspaceStepState.Error => glyphs.Error,
        WorkspaceStepState.Running => $"[blue]{spinner}[/]",
        _ => glyphs.Pending,
    };

    // The state to show on a repo heading, rolled up from its steps: a failure or an in-flight step
    // dominates; otherwise done once every step is, else still pending.
    static WorkspaceStepState Aggregate(IEnumerable<StepRow> group)
    {
        var states = group.Select(x => x.State).ToList();
        if (states.Contains(WorkspaceStepState.Error)) return WorkspaceStepState.Error;
        if (states.Contains(WorkspaceStepState.Running)) return WorkspaceStepState.Running;
        // Some done and some not-yet-done → the group is mid-flight.
        if (states.Contains(WorkspaceStepState.Done) && states.Contains(WorkspaceStepState.Pending))
            return WorkspaceStepState.Running;
        if (states.Contains(WorkspaceStepState.Warning)) return WorkspaceStepState.Warning;
        return states.All(x => x == WorkspaceStepState.Done) ? WorkspaceStepState.Done : WorkspaceStepState.Pending;
    }

    // Strip ANSI escape sequences and control characters from streamed command output before it reaches
    // the markup renderer — a stray cursor-move or tab from npm would otherwise shift every row beneath.
    static readonly Regex AnsiControl = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B.|[\x00-\x1F\x7F]", RegexOptions.Compiled);
    static string Clean(string s) => AnsiControl.Replace(s, "").Trim();

    static string Truncate(string s, int max = 100) => s.Length <= max ? s : s[..(max - 1)] + "...";

    // Glyphs chosen for what the terminal can actually draw: nice marks + a braille spinner when it
    // reports Unicode support, plain ASCII otherwise — so a limited console shows real characters,
    // never a substituted '?'. Markers carry their own colour markup.
    readonly record struct Glyphs(string Done, string Warn, string Error, string Pending, string[] Spinner);

    static Glyphs GlyphsFor(IAnsiConsole console) =>
        console.Profile.Capabilities.Unicode
            ? new Glyphs("[green]✓[/]", "[yellow]![/]", "[red]✗[/]", "[grey]○[/]",
                ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"])
            : new Glyphs("[green]+[/]", "[yellow]![/]", "[red]x[/]", "[grey].[/]",
                ["|", "/", "-", "\\"]);

    sealed class StepRow
    {
        public StepRow(string id, string label, bool subStep)
        {
            Id = id;
            SubStep = subStep;
            Repo = RepoOf(id);
            // The Core labels repo-scoped steps "<action> — <repo>". The repo now heads the group, so
            // drop that suffix from the child's label; setup sub-steps are raw commands, left as-is.
            Label = !subStep && Repo is { } r ? StripRepoSuffix(label, r) : label;
        }

        public string Id { get; }
        public string Label { get; }
        public bool SubStep { get; }

        /// <summary>The repo this step belongs to (from the step id's prefix), or null for the
        /// workspace-level steps (allocate/release ports, save/delete record).</summary>
        public string? Repo { get; }

        public WorkspaceStepState State { get; set; } = WorkspaceStepState.Pending;
        public string? Detail { get; set; }
        public string? Output { get; set; }

        // Step ids are "ports"/"record" (no repo) or "<repo>:<verb>[:<n>]" — the repo is the prefix.
        static string? RepoOf(string id)
        {
            var colon = id.IndexOf(':');
            return colon > 0 ? id[..colon] : null;
        }

        static string StripRepoSuffix(string label, string repo)
        {
            var idx = label.LastIndexOf(repo, StringComparison.Ordinal);
            if (idx <= 0) return label;
            var head = label[..idx].TrimEnd().TrimEnd('—', '-').TrimEnd();
            return head.Length > 0 ? head : label;
        }
    }
}

/// <summary>The Unicode tick / ASCII '+' used by the post-op summary lines, matching the checklist's
/// capability-based glyph choice.</summary>
static class Glyph
{
    public static string Check(IAnsiConsole console) => console.Profile.Capabilities.Unicode ? "✔" : "+";
}
