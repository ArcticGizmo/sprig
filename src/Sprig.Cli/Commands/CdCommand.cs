using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sprig.Core.Config;
using Sprig.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// Two front-ends over one resolution: `sprig path` prints a workspace repo/module directory (the scripting
// primitive — and the backbone of a future cd-in-place shell wrapper), and `sprig cd` opens a new terminal
// window sitting in that directory. Both share TargetResolver; neither duplicates the workspace→repo→module
// walk. `cd` spawns a window because a process can't change its parent shell's cwd — so rather than move the
// shell you're in, we open a fresh one in the target (matching the shell you came from, and a fresh WT window
// under Windows Terminal).

/// <summary>Shared workspace → repo → module resolution behind <c>sprig path</c> and <c>sprig cd</c>.
/// Positional resolution is non-interactive; the pickers render on <b>stderr</b> so a captured stdout
/// (a shell wrapper doing <c>Set-Location (sprig path …)</c>, or <c>$p = sprig path …</c>) stays a clean
/// path. A resolved-but-missing worktree throws — navigation never silently points at a drifted tree.</summary>
sealed class TargetResolver(CliContext cli)
{
    public readonly record struct Target(string Path, string Workspace, string Repo, string Module);

    /// <summary>Resolve to a target, or null when an interactive pick is cancelled (ESC). Throws on a bad
    /// argument (unknown workspace/repo/module, missing terminal for <c>-i</c>) or a missing worktree. When
    /// interactive, any args passed are honoured as presets and only the gaps are prompted for — so
    /// <c>cd feat</c> at a terminal still asks which repo/module, while <c>cd feat api web</c> goes straight
    /// in.</summary>
    public Target? Resolve(bool interactive, string? workspace, string? repo, string? module)
    {
        var target = interactive ? ResolveInteractive(workspace, repo, module) : ResolveFromArgs(workspace, repo, module);
        if (target is { } t && !Directory.Exists(t.Path))
            throw new DirectoryNotFoundException(
                $"worktree missing at {t.Path} — the workspace may have drifted; try 'sprig ws reconcile {t.Workspace}'");
        return target;
    }

    // Non-interactive resolution from the positionals. Missing repo is inferred only when the workspace
    // has a single repo; a missing module always means the repo root.
    Target ResolveFromArgs(string? workspace, string? repoName, string? moduleName)
    {
        var name = workspace ?? throw new ArgumentException("a workspace is required (or run it at a terminal to be prompted)");
        var record = cli.Workspaces.Get(name) ?? throw new ArgumentException($"unknown workspace '{name}'");

        var repo = PickRepo(record, repoName);
        var (path, module) = PickModule(repo, moduleName);
        return new Target(path, record.Workspace, repo.Name, module);
    }

    static InstanceRepo PickRepo(InstanceRecord record, string? name)
    {
        if (record.Repos.Count == 0)
            throw new ArgumentException($"workspace '{record.Workspace}' has no repos");

        if (name is null)
        {
            if (record.Repos.Count == 1) return record.Repos[0];
            throw new ArgumentException(
                $"workspace '{record.Workspace}' has {record.Repos.Count} repos — name one: " +
                string.Join(", ", record.Repos.Select(r => r.Name)));
        }

        return record.Repos.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"workspace '{record.Workspace}' has no repo '{name}' (it has: " +
                string.Join(", ", record.Repos.Select(r => r.Name)) + ")");
    }

    // Resolve a module argument to a directory under the worktree. null → the worktree root; "root"/"." →
    // the root explicitly; otherwise a declared module by name (its Path joined onto the worktree).
    (string Path, string Module) PickModule(InstanceRepo repo, string? name)
    {
        if (name is null || IsRoot(name))
            return (repo.WorktreePath, RootLabel);

        var modules = ModulesOf(repo);
        var match = modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
        if (match is null)
        {
            var choices = new[] { RootLabel }.Concat(modules.Where(m => m.Path.Length > 0).Select(m => m.Name));
            throw new ArgumentException($"repo '{repo.Name}' has no module '{name}' (it has: {string.Join(", ", choices)})");
        }
        return (ModulePath(repo.WorktreePath, match), match.Name);
    }

    // The interactive picker, gap-filling from whatever presets were passed: workspace (skipped when named
    // or lone) → repo (skipped when named or the workspace has one) → module (honoured when named, else
    // picked, else the root when the repo has no sub-modules). ESC at any step cancels the whole thing. All
    // UI goes to stderr so `sprig path` still yields a clean path on stdout. Only reached once Interactivity
    // has confirmed a terminal is attached, so it never blocks on a redirected stdin.
    Target? ResolveInteractive(string? workspacePreset, string? repoPreset, string? modulePreset)
    {
        var console = Term.CreateError();

        var records = cli.Workspaces.List();
        if (records.Count == 0)
        {
            console.MarkupLine("[yellow]no workspaces yet — create one with 'sprig create'[/]");
            return null;
        }

        InstanceRecord workspace;
        if (workspacePreset is { } preset)
            workspace = cli.Workspaces.Get(preset) ?? throw new ArgumentException($"unknown workspace '{preset}'");
        else if (records.Count == 1)
            workspace = records[0];
        else if (Term.SelectOne(console, "Select a [green]workspace[/]: [grey](esc cancels)[/]",
                     records.Select(r => r.Workspace).OrderBy(x => x, StringComparer.Ordinal)) is { } pick)
            workspace = cli.Workspaces.Get(pick)!;
        else
            return Cancelled(console);

        var repo = PickRepoInteractive(console, workspace, repoPreset);
        if (repo is null) return Cancelled(console);

        // A named module is honoured (and validated) rather than re-asked, so a fully-specified navigation
        // never stops to prompt; otherwise offer the picker (which defaults to the root).
        (string Path, string Module)? module = modulePreset is not null
            ? PickModule(repo, modulePreset)
            : PickModuleInteractive(console, repo);
        if (module is null) return Cancelled(console);

        return new Target(module.Value.Path, workspace.Workspace, repo.Name, module.Value.Module);
    }

    InstanceRepo? PickRepoInteractive(IAnsiConsole console, InstanceRecord record, string? preset)
    {
        if (preset is not null) return PickRepo(record, preset);
        if (record.Repos.Count <= 1) return record.Repos.Count == 1 ? record.Repos[0] : null;

        // Which workspace this pick is for — the workspace may have been auto-chosen (a lone one) or passed
        // as an argument, so the user might never have seen its name before the repo list appears.
        console.MarkupLine($"[bold]Workspace[/] [green]{Markup.Escape(record.Workspace)}[/]");
        return Term.SelectOne(console, "Which [green]repo[/]? [grey](esc cancels)[/]",
                record.Repos.Select(r => r.Name).OrderBy(x => x, StringComparer.Ordinal)) is { } pick
            ? PickRepo(record, pick)
            : null;
    }

    // Offer the worktree root (always first) plus each sub-module directory. A repo with no sub-modules
    // (a flat/single-module repo) has nothing to choose, so we skip straight to the root.
    (string Path, string Module)? PickModuleInteractive(IAnsiConsole console, InstanceRepo repo)
    {
        var subModules = ModulesOf(repo).Where(m => m.Path.Length > 0).ToList();
        if (subModules.Count == 0) return (repo.WorktreePath, RootLabel);

        var choices = new List<string> { RootLabel };
        choices.AddRange(subModules.Select(m => m.Name));
        if (Term.SelectOne(console, $"Which [green]module[/] of [bold]{Markup.Escape(repo.Name)}[/]? [grey](esc cancels)[/]",
                choices) is not { } pick)
            return null;

        return pick == RootLabel
            ? (repo.WorktreePath, RootLabel)
            : (ModulePath(repo.WorktreePath, subModules.First(m => m.Name == pick)), pick);
    }

    // A repo's declared modules, loaded from the worktree's committed .sprig.json. Best-effort: a repo
    // whose config can't be read still resolves to its root, so navigation never depends on a parseable config.
    static IReadOnlyList<ModuleDeclaration> ModulesOf(InstanceRepo repo)
    {
        try { return SprigConfigLoader.LoadFromFile(Path.Combine(repo.WorktreePath, ".sprig.json")).EffectiveModules; }
        catch (SprigConfigException) { return []; }
    }

    // A module's directory: its (slash-separated) Path joined onto the worktree, matching how the Core
    // resolves a module's working directory (WorkspaceService.RunSetup).
    static string ModulePath(string worktree, ModuleDeclaration module)
        => module.Path.Length == 0
            ? worktree
            : Path.Combine(worktree, module.Path.Replace('/', Path.DirectorySeparatorChar));

    const string RootLabel = "(root)";
    static bool IsRoot(string name) => name is "root" or "(root)" or ".";

    static Target? Cancelled(IAnsiConsole console)
    {
        console.MarkupLine("[yellow]cancelled[/]");
        return null;
    }
}

// `sprig path` — resolve and print a workspace repo/module directory. The scripting primitive: a bare path
// on stdout (or --json for the structured target), all pickers on stderr, so `Set-Location (sprig path feat
// api)` and `$p = sprig path -i` both yield a clean path. `sprig cd` is the human front-end over the same
// resolution; machine callers that want a directory come here, not to `cd`.
[Description("Print a workspace repo/module directory (for scripting and shell wrappers)")]
public sealed class PathCommand(CliContext cli) : Command<PathCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[workspace]")]
        [Description("Workspace (omit at a terminal to pick interactively)")]
        public string? Workspace { get; set; }

        [CommandArgument(1, "[repo]")]
        [Description("Repo within the workspace (optional; implied when the workspace has one repo)")]
        public string? Repo { get; set; }

        [CommandArgument(2, "[module]")]
        [Description("Module within the repo (optional; defaults to the repo root)")]
        public string? Module { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive picker (workspace, repo, module)")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking (implied by --json, a pipe, or CI)")]
        public bool NoInteractive { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        // At a terminal, gap-fill: any args passed are honoured, the rest is picked. In a script, a pipe, or
        // CI (or under --ni/--json) it resolves straight from the args instead. hasPrimaryInput stays false
        // so naming the workspace still lets the terminal prompt for an ambiguous repo/module.
        var interactive = Interactivity.Resolve(s.Interactive, s.NoInteractive, s.Json, hasPrimaryInput: false);

        var target = new TargetResolver(cli).Resolve(interactive, s.Workspace, s.Repo, s.Module);
        if (target is not { } t)
            return 0; // interactive cancel (ESC): nothing to print

        if (s.Json)
        {
            CliOutput.Json(new { ok = true, path = t.Path, workspace = t.Workspace, repo = t.Repo, module = t.Module });
            return 0;
        }

        Console.WriteLine(t.Path);
        return 0;
    }
}

// `sprig cd` — open a new terminal window sitting in a workspace's repo/module directory, in the shell you
// came from (powershell → powershell, cmd → cmd, …) and, under Windows Terminal, a fresh WT window. Resolves
// via the same TargetResolver as `sprig path`; it has no machine-output flags — scripts use `sprig path`.
[Description("Open a new terminal window in a workspace repo/module directory")]
public sealed class CdCommand(CliContext cli) : Command<CdCommand.Settings>
{
    // Deliberately not GlobalSettings: `cd`'s job is to open a window, so it carries no --json (there is no
    // machine output to promise) — a `--json` here fails strict parsing rather than silently doing nothing.
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[workspace]")]
        [Description("Workspace (omit at a terminal to pick interactively)")]
        public string? Workspace { get; set; }

        [CommandArgument(1, "[repo]")]
        [Description("Repo within the workspace (optional; implied when the workspace has one repo)")]
        public string? Repo { get; set; }

        [CommandArgument(2, "[module]")]
        [Description("Module within the repo (optional; defaults to the repo root)")]
        public string? Module { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive picker (workspace, repo, module)")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking (implied by a pipe or CI)")]
        public bool NoInteractive { get; set; }

        [CommandOption("--shell <name>")]
        [Description("Shell to open (powershell|pwsh|cmd|bash|…); default: match the one you came from")]
        public string? Shell { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        // At a terminal, gap-fill: honour any args, pick the rest (so `cd feat` still asks which repo/module,
        // `cd feat api web` goes straight in). Without a terminal it resolves from the args. `cd` has no
        // --json, so interactivity is simply terminal-and-not-`--ni`.
        var interactive = Interactivity.Resolve(s.Interactive, s.NoInteractive, json: false, hasPrimaryInput: false);

        var target = new TargetResolver(cli).Resolve(interactive, s.Workspace, s.Repo, s.Module);
        if (target is not { } t)
            return 0; // interactive cancel (ESC): nothing to open

        // Opening a window is Windows-only for now; elsewhere we can't spawn a matching terminal reliably yet
        // (that's coming), so degrade to printing the path and point at `sprig path` for scripting.
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("note: opening a window is Windows-only for now — printing the path (use 'sprig path' for scripting)");
            Console.WriteLine(t.Path);
            return 0;
        }

        var shell = LaunchWindow(t.Path, s.Shell);
        cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] opened [bold]{Markup.Escape(shell)}[/] " +
            $"in a new window — [dim]{Markup.Escape(t.Path)}[/]");
        return 0;
    }

    // Open a new terminal window running a shell in `dir`. Tries the best-matching shell first (the one we
    // were invoked from), then falls back to pwsh/powershell/cmd if that one won't launch. Returns the
    // display name of whichever opened.
    static string LaunchWindow(string dir, string? shellOverride)
    {
        Win32Exception? last = null;
        foreach (var shell in ShellCandidates(shellOverride))
        {
            try { Start(shell, dir); return Path.GetFileNameWithoutExtension(shell); }
            catch (Win32Exception ex) { last = ex; } // e.g. pwsh not installed → try the next candidate
        }
        throw (Exception?)last ?? new InvalidOperationException("no shell available to open a window with");
    }

    static void Start(string shell, string dir)
    {
        // Inside Windows Terminal, open a fresh WT window (-w new) so the emulator matches too — not a bare
        // legacy console. If wt isn't resolvable, fall through to a plain new console window.
        if (Environment.GetEnvironmentVariable("WT_SESSION") is { Length: > 0 })
        {
            try
            {
                var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
                foreach (var arg in new[] { "-w", "new", "-d", dir, shell }) wt.ArgumentList.Add(arg);
                Process.Start(wt);
                return;
            }
            catch (Win32Exception) { /* wt not found → plain console window below */ }
        }

        Process.Start(new ProcessStartInfo(shell) { UseShellExecute = true, WorkingDirectory = dir });
    }

    // Candidate shells, best first: an explicit --shell, else the shell we were launched from (when it's a
    // known one), then pwsh/powershell/cmd as universal fallbacks.
    static IEnumerable<string> ShellCandidates(string? shellOverride)
    {
        if (shellOverride is { } o)
            yield return ResolveShellName(o);
        else if (ParentProcessPath() is { } parent && KnownShells.Contains(Path.GetFileName(parent)))
            yield return parent;

        if (Which("pwsh.exe") is { } pwsh) yield return pwsh;
        if (Which("powershell.exe") is { } ps) yield return ps;
        yield return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    }

    static readonly HashSet<string> KnownShells = new(StringComparer.OrdinalIgnoreCase)
        { "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "sh.exe", "zsh.exe", "nu.exe", "fish.exe" };

    static string ResolveShellName(string name) => name.ToLowerInvariant() switch
    {
        "powershell" => "powershell.exe",
        "pwsh" => "pwsh.exe",
        "cmd" => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        "bash" => "bash.exe",
        "zsh" => "zsh.exe",
        _ => name, // a bare exe name or a full path — passed through
    };

    static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir.Trim(), exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // The full path of the process that started us — the shell, when run from a prompt — via the PPID from
    // NtQueryInformationProcess. Best-effort: any failure (non-Windows, access denied, exited parent) is
    // swallowed and the shell-matching just falls back to a default.
    static string? ParentProcessPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var self = Process.GetCurrentProcess();
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0)
                return null;
            var ppid = info.InheritedFromUniqueProcessId.ToInt32();
            if (ppid <= 0) return null;
            using var parent = Process.GetProcessById(ppid);
            return parent.MainModule?.FileName;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);
}
