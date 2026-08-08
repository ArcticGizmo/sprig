using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sprig.Core.Config;
using Sprig.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig cd` — resolve the directory of a workspace's repo (and optionally a module within it) and open a
// new terminal window sitting in it. A process can't change its parent shell's cwd, so rather than move
// the shell you're in, we spawn a fresh one in the target directory — matching the shell you came from
// (powershell → powershell, cmd → cmd, …) and, under Windows Terminal, a fresh WT window too.
//
// --print / --json resolve without launching (for scripting); a redirected stdout falls back to printing
// too, so `x = $(sprig cd feat api)` still yields a path instead of popping a window at a pipe.
[Description("Open a new terminal window in a workspace repo/module directory")]
public sealed class CdCommand(CliContext cli) : Command<CdCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[workspace]")]
        [Description("Workspace (omit with -i)")]
        public string? Workspace { get; set; }

        [CommandArgument(1, "[repo]")]
        [Description("Repo within the workspace (optional; implied when the workspace has one repo)")]
        public string? Repo { get; set; }

        [CommandArgument(2, "[module]")]
        [Description("Module within the repo (optional; defaults to the repo root)")]
        public string? Module { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Pick workspace, repo and module interactively")]
        public bool Interactive { get; set; }

        [CommandOption("--print")]
        [Description("Print the resolved path instead of opening a window")]
        public bool Print { get; set; }

        [CommandOption("--shell <name>")]
        [Description("Shell to open (powershell|pwsh|cmd|bash|…); default: match the one you came from")]
        public string? Shell { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.Interactive && s.Json)
            throw new ArgumentException("-i is interactive — it can't be combined with --json");

        var target = s.Interactive ? ResolveInteractive(s) : ResolveFromArgs(s);
        if (target is not { } t)
            return 0; // interactive cancel (ESC): nothing to open

        if (!Directory.Exists(t.Path))
            throw new DirectoryNotFoundException(
                $"worktree missing at {t.Path} — the workspace may have drifted; try 'sprig ws reconcile {t.Workspace}'");

        if (s.Json)
        {
            CliOutput.Json(new { ok = true, path = t.Path, workspace = t.Workspace, repo = t.Repo, module = t.Module });
            return 0;
        }

        // --print, non-Windows (no reliable new-window story), or a redirected stdout → just emit the path.
        if (s.Print || !OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            if (!s.Print && !OperatingSystem.IsWindows())
                Console.Error.WriteLine("note: opening a window is Windows-only — printing the path");
            Console.WriteLine(t.Path);
            return 0;
        }

        var shell = LaunchWindow(t.Path, s.Shell);
        Console.WriteLine($"opened {shell} in a new window — {t.Path}");
        return 0;
    }

    readonly record struct Target(string Path, string Workspace, string Repo, string Module);

    // Non-interactive resolution from the positionals. Missing repo is inferred only when the workspace
    // has a single repo; a missing module always means the repo root.
    Target ResolveFromArgs(Settings s)
    {
        var workspace = s.Workspace ?? throw new ArgumentException("cd requires a workspace (or use -i)");
        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");

        var repo = PickRepo(record, s.Repo);
        var (path, module) = PickModule(repo, s.Module);
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

    // The interactive picker: workspace → repo (skipped when there's one) → module (skipped when the repo
    // has no sub-module directories, landing at the root). ESC at any step cancels the whole thing. All UI
    // goes to stderr so `-i --print` still yields a clean path on stdout.
    Target? ResolveInteractive(Settings s)
    {
        if (Console.IsInputRedirected)
            throw new ArgumentException("-i needs an interactive terminal (stdin is redirected)");

        var console = Term.CreateError();

        var records = cli.Workspaces.List();
        if (records.Count == 0)
        {
            console.MarkupLine("[yellow]no workspaces yet — create one with 'sprig create'[/]");
            return null;
        }

        InstanceRecord workspace;
        if (s.Workspace is { } preset)
            workspace = cli.Workspaces.Get(preset) ?? throw new ArgumentException($"unknown workspace '{preset}'");
        else if (records.Count == 1)
            workspace = records[0];
        else if (Term.SelectOne(console, "Select a [green]workspace[/]: [grey](esc cancels)[/]",
                     records.Select(r => r.Workspace).OrderBy(x => x, StringComparer.Ordinal)) is { } pick)
            workspace = cli.Workspaces.Get(pick)!;
        else
            return Cancelled(console);

        var repo = PickRepoInteractive(console, workspace, s.Repo);
        if (repo is null) return Cancelled(console);

        var module = PickModuleInteractive(console, repo);
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
    // whose config can't be read still cds to its root, so navigation never depends on a parseable config.
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
