using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Shell;
using Sprig.Engine.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for <c>sprig cd</c> and <c>sprig path</c>: a workspace, then a place in it.</summary>
public class PlaceSettings : WorkspaceSettings
{
    [CommandArgument(2, "[place]")]
    [Description("Where in the workspace: root (its own folder), a repo, or a folder its repos name under cd: (default: root)")]
    public string? Place { get; set; }

    // A word that isn't one of the ID's workspaces is the place, so `sprig cd api` stays in this workspace.
    protected override bool TakesApps => true;
}

/// <summary>Which workspace and which of its places a <c>sprig cd</c> or <c>sprig path</c> means: as named, with any gap
/// asked about at a terminal, else the workspace this folder is in and its root.</summary>
static class PlaceTarget
{
    public static (WorkspaceRecord Record, WorkspacePlace Place)? Resolve(CliContext cli, PlaceSettings s, bool interactive)
    {
        var named = !string.IsNullOrEmpty(s.Orchestration) || !string.IsNullOrEmpty(s.Workspace) || s.Place is not null;
        WorkspaceRecord record;
        var words = new List<string>();
        if (!named && interactive)
        {
            var here = Here.TryFind(cli);
            var choices = Choices(cli, here?.Id);
            if (choices.Count == 0) throw new InvalidOperationException("there are no workspaces yet; start one with: sprig up");
            var current = here is { } h ? Record(cli, h.Id, h.Workspace ?? WorkspaceRecord.MainName) : null;
            record = choices.Count == 1 ? choices[0] : PickWorkspace(cli, choices, current, showId: here is null);
        }
        else
        {
            var (first, second) = (s.Orchestration, s.Workspace);
            words.AddRange(s.Resolve(cli));
            // Just an ID, at a terminal: which of its workspaces is asked, rather than taken to be main.
            var onlyId = interactive && string.IsNullOrEmpty(second) && words.Count == 0 && s.Place is null
                         && string.Equals(first, s.Orchestration, StringComparison.OrdinalIgnoreCase);
            if (onlyId && Choices(cli, s.Orchestration) is { Count: > 1 } ofId)
                record = PickWorkspace(cli, ofId, Here.TryFind(cli) is { } h && h.Id == s.Orchestration
                    ? Record(cli, h.Id, h.Workspace ?? WorkspaceRecord.MainName) : null, showId: false);
            else
                record = Record(cli, s.Orchestration, s.Workspace)
                         ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace '{s.Workspace}'; see them with: sprig workspaces {s.Orchestration}");
        }
        if (s.Place is { } place) words.Add(place);
        if (words.Count > 1) throw new InvalidOperationException($"name one place to go to, not {string.Join(" and ", words.Select(w => $"'{w}'"))}");

        var places = WorkspacePlaces.Of(record, PlanOf(cli, record), FallbackRoot(cli, record.Orchestration));
        if (places.Count == 0) throw new InvalidOperationException($"{record.SlotName} has no folder yet; bring it up first: sprig up {record.Orchestration}");
        var chosen = words.Count == 1
            ? WorkspacePlaces.Find(places, words[0])
              ?? throw new InvalidOperationException($"{record.SlotName} has no place '{words[0]}'; it has {string.Join(", ", places.Select(p => p.Name))}")
            : interactive && places.Count > 1 ? PickPlace(cli, record, places)
            : places[0];
        if (!Directory.Exists(chosen.Dir))
            throw new DirectoryNotFoundException($"{chosen.Dir} isn't there; {record.SlotName} may need making ready again: sprig up {record.Orchestration} {record.Name}");
        return (record, chosen);
    }

    /// <summary>The workspace's stored record; for main, which exists before it has ever come up, one made for it.</summary>
    static WorkspaceRecord? Record(CliContext cli, string id, string workspace)
        => cli.Workspaces.Get(id, workspace)
           ?? (workspace == WorkspaceRecord.MainName ? cli.Workspaces.MainOf(id, cli.Orchestrations.Resolve(id) ?? "") : null);

    /// <summary>Its own last plan, else another workspace's of the same ID: what the repos name under <c>cd:</c> is the
    /// same for every branch more often than not, and a main never brought up has no plan of its own.</summary>
    static Plan? PlanOf(CliContext cli, WorkspaceRecord record)
        => cli.Workspaces.LastPlan(record.Orchestration, record.Name)
           ?? cli.Workspaces.List().Where(r => r.Orchestration == record.Orchestration)
               .Select(r => cli.Workspaces.LastPlan(r.Orchestration, r.Name)).FirstOrDefault(p => p is not null);

    /// <summary>For a workspace with no folders recorded: the repo's folder on the Repos list, else the orchestration
    /// file's folder.</summary>
    static string? FallbackRoot(CliContext cli, string id)
        => new StoredDefinitions(cli.Paths).Repo(id)?.Folder
           ?? (cli.Orchestrations.Resolve(id) is { } file && File.Exists(file) ? Path.GetDirectoryName(Path.GetFullPath(file)) : null);

    /// <summary>The ID's workspaces (main included) or, outside every repo, every workspace: each ID's main first, then
    /// its slots by number.</summary>
    static IReadOnlyList<WorkspaceRecord> Choices(CliContext cli, string? id)
    {
        var records = cli.Workspaces.List().Where(r => id is null || r.Orchestration == id).ToList();
        if (id is not null && records.All(r => !r.IsMain) && Record(cli, id, WorkspaceRecord.MainName) is { } main) records.Add(main);
        return records
            .OrderBy(r => r.Orchestration, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.IsMain ? 0 : 1)
            .ThenBy(r => int.TryParse(r.Name, out var n) ? n : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>A workspace as a list shows it: its folder's name, then the branch it's on, or that it's free.</summary>
    public static string Describe(WorkspaceRecord record, bool markup = true)
    {
        var branch = record.IsFree ? "free" : record.IsMain ? $"{record.CheckoutBranch ?? "main"} (main checkout)" : record.Label;
        if (!markup) return $"{record.SlotName} · {branch}";
        var shown = record.IsFree ? $"[yellow]{branch}[/]" : $"[green]{Markup.Escape(branch)}[/]";
        return $"{Markup.Escape(record.SlotName)}  {shown}";
    }

    static WorkspaceRecord PickWorkspace(CliContext cli, IReadOnlyList<WorkspaceRecord> choices, WorkspaceRecord? current, bool showId)
    {
        // The one this folder is in leads, so Enter stays put and arrows go elsewhere.
        var ordered = current is null ? choices : [.. choices.Where(c => c.Key == current.Key), .. choices.Where(c => c.Key != current.Key)];
        return cli.Ansi.Prompt(new SelectionPrompt<WorkspaceRecord>()
            .Title(showId ? "Which workspace?" : $"Which workspace of [bold]{Markup.Escape(choices[0].Orchestration)}[/]?")
            .PageSize(15)
            .UseConverter(r => Describe(r) + (current is not null && r.Key == current.Key ? "  [dim](here)[/]" : ""))
            .AddChoices(ordered));
    }

    static WorkspacePlace PickPlace(CliContext cli, WorkspaceRecord record, IReadOnlyList<WorkspacePlace> places)
        => cli.Ansi.Prompt(new SelectionPrompt<WorkspacePlace>()
            .Title($"Where in {Describe(record)}?")
            .PageSize(15)
            .UseConverter(p => $"{Markup.Escape(p.Name)}  [dim]{Markup.Escape(p.What)}[/]")
            .AddChoices(places));
}

[Description("Print the folder of a workspace, or of a place in it (for scripts): root, a repo, or a folder its repos name under cd:")]
public sealed class PathCommand(CliContext cli) : Command<PlaceSettings>
{
    protected override int Execute(CommandContext context, PlaceSettings s, CancellationToken cancellation)
    {
        // Never asks: a script reading the path has no one to answer.
        var (record, place) = PlaceTarget.Resolve(cli, s, interactive: false)!.Value;
        if (s.Json)
        {
            CliOutput.Json(new { ok = true, path = place.Dir, place = place.Name, id = record.Orchestration, workspace = record.Name, branch = record.Branch, free = record.IsFree });
            return 0;
        }
        Console.WriteLine(place.Dir);
        return 0;
    }
}

/// <summary>
/// Moves the shell it was typed in, when sprig runs through one of its shell shims (see <see cref="ShellShims"/>);
/// otherwise opens a new terminal window there, in the shell it came from (under Windows Terminal, a new Windows
/// Terminal window), or elsewhere than Windows prints the folder.
/// </summary>
[Description("Go to a workspace's folder, or a place in it: root, a repo, or a folder its repos name under cd:. With nothing named at a terminal, pick from a list that shows each workspace's branch, or that it's free")]
public sealed class CdCommand(CliContext cli) : Command<CdCommand.Settings>
{
    public sealed class Settings : PlaceSettings
    {
        [CommandOption("--window")]
        [Description("Open a new window even when this shell could be moved")]
        public bool Window { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var (record, place) = PlaceTarget.Resolve(cli, s, cli.Interactive(s.Json))!.Value;
        if (s.Json)
        {
            CliOutput.Json(new { ok = true, path = place.Dir, place = place.Name, id = record.Orchestration, workspace = record.Name, branch = record.Branch, free = record.IsFree });
            return 0;
        }

        var where = $"{PlaceTarget.Describe(record)}{(place.Name == DefinitionReader.CdRoot ? "" : $"  [dim]·[/] {Markup.Escape(place.Name)}")}";
        if (!s.Window && ShellShims.TryRequestCd(place.Dir))
        {
            cli.Ansi.MarkupLine($"[green]→[/] {where}");
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("note: this shell can't be moved and opening a window is Windows-only, so here's the folder (cd \"$(sprig path ...)\" moves a shell)");
            Console.WriteLine(place.Dir);
            return 0;
        }

        var calling = CallingShell();
        var shell = TerminalWindow.Open(place.Dir, calling?.Path, PlaceTarget.Describe(record, markup: false));
        cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] opened {Markup.Escape(shell)} in a new window at {where}");
        if (calling is { ThroughCmdShim: true } c && TerminalWindow.IsPowerShell(c.Path) && PowerShellShimMissing())
            cli.Ansi.MarkupLine("[dim]tip: sprig cd can move this PowerShell window instead; sprig shell install says how[/]");
        return 0;
    }

    // Only an installed sprig has a shims folder that `sprig shell install` can add sprig.ps1 to.
    static bool PowerShellShimMissing()
    {
        var cliDir = Path.GetDirectoryName(Environment.ProcessPath);
        return cliDir is not null && ShellInstall.IsInstalled(cliDir)
            && !File.Exists(Path.Combine(ShellInstall.ShimDirectory(cliDir), "sprig.ps1"));
    }

    readonly record struct Calling(string Path, bool ThroughCmdShim);

    /// <summary>
    /// The known shell sprig was typed in, when it can be told: usually the parent process. From PowerShell without
    /// sprig.ps1, sprig runs through sprig.cmd in a cmd started with /c just for it, so the shell is the one that
    /// started that cmd. A shim offers a cmd the user is sitting in a directives file, so a cmd parent without one is
    /// the throwaway kind. Best-effort: any failure falls back to a default shell.
    /// </summary>
    static Calling? CallingShell()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var self = Process.GetCurrentProcess();
            using var parent = ParentOf(self);
            if (parent?.MainModule?.FileName is not { } path) return null;
            if (string.Equals(Path.GetFileName(path), "cmd.exe", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ShellShims.DirectivesVariable)))
            {
                using var grandparent = ParentOf(parent);
                if (grandparent?.MainModule?.FileName is { } outer && TerminalWindow.KnownShells.Contains(Path.GetFileName(outer)))
                    return new Calling(outer, ThroughCmdShim: true);
            }
            return TerminalWindow.KnownShells.Contains(Path.GetFileName(path)) ? new Calling(path, ThroughCmdShim: false) : null;
        }
        catch { return null; }
    }

    static Process? ParentOf(Process child)
    {
        var info = new ProcessBasicInformation();
        if (NtQueryInformationProcess(child.Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0) return null;
        var ppid = info.InheritedFromUniqueProcessId.ToInt32();
        return ppid <= 0 ? null : Process.GetProcessById(ppid);
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
