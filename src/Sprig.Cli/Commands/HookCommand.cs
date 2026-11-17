using System.ComponentModel;
using System.Text.Json;
using Sprig.Engine.Definitions;
using Sprig.Engine.Git;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Processes;
using Sprig.Engine.Store;
using Sprig.Engine.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>
/// What the Claude Code plugins' hooks run. A hook is one line in a plugin, so the behaviour lives here, versioned with
/// sprig; the plugin passes the contract it was written for, and a contract this sprig doesn't serve is refused with
/// which side to update, since sprig and the plugins update separately.
/// </summary>
[Description("Run a Claude Code hook: worktree-create, worktree-remove or session-start. The sprig plugins call this; it reads the hook's JSON on stdin")]
public sealed class HookCommand(CliContext cli) : Command<HookCommand.Settings>
{
    /// <summary>The contracts this sprig serves, oldest first. Add one when a hook's input or output changes in a way an
    /// older plugin or sprig would misread, and keep serving the one before it for a while.</summary>
    internal static readonly int[] Contracts = [1];

    internal static readonly string[] Hooks = ["worktree-create", "worktree-remove", "session-start"];

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hook>")]
        [Description("worktree-create, worktree-remove or session-start")]
        public string Hook { get; set; } = "";

        [CommandOption("--contract <n>")]
        [Description("The hook contract the plugin was written for")]
        public int Contract { get; set; }
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => Hooks.Contains(s.Hook) ? ValidationResult.Success() : ValidationResult.Error($"the hook is one of {string.Join(", ", Hooks)}");

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (!Contracts.Contains(s.Contract))
        {
            // A session must start even with a plugin and a sprig that don't match; it just starts without sprig's context.
            Console.Error.WriteLine(s.Contract > Contracts[^1]
                ? $"sprig: this plugin needs a newer sprig (hook contract {s.Contract}; this one serves up to {Contracts[^1]}). Run: sprig update"
                : $"sprig: this plugin is older than this sprig serves (hook contract {s.Contract}; this one serves from {Contracts[0]}). Update it with /plugin");
            return s.Hook == "session-start" ? 0 : 2;
        }

        var input = ReadInput();
        try
        {
            return s.Hook switch
            {
                "worktree-create" => WorktreeCreate(input, cancellation),
                "worktree-remove" => WorktreeRemove(input, cancellation),
                _ => SessionStart(input),
            };
        }
        catch (Exception e) when (s.Hook == "session-start")
        {
            Console.Error.WriteLine($"sprig: {e.Message}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"sprig: {e.Message}");
            return 1;
        }
    }

    sealed record HookInput(string Cwd, string? Name, string? WorktreePath, string? SessionId);

    /// <summary>Where the hook's JSON is read from: stdin unless a test gives it.</summary>
    internal static TextReader? Input { get; set; }

    static HookInput ReadInput()
    {
        // At a terminal there's no hook input to wait for.
        var text = Input?.ReadToEnd() ?? (Console.IsInputRedirected ? Console.In.ReadToEnd() : "");
        if (string.IsNullOrWhiteSpace(text)) return new HookInput(Environment.CurrentDirectory, null, null, null);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        string? Field(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new HookInput(Field("cwd") ?? Environment.CurrentDirectory, Field("name"), Field("worktree_path"), Field("session_id"));
    }

    static readonly GitClient Git = new(new ProcessRunner());

    /// <summary>What Claude Code calls a worktree's branch when it makes one itself, kept so the fallback matches it.</summary>
    static string BranchFor(string name) => $"worktree-{name}";

    /// <summary>
    /// In a repo or workspace sprig knows, the new worktree is a slot on its own branch, made ready (worktrees, ports,
    /// files, its infra's definitions) with nothing started; elsewhere it's the plain worktree Claude Code would have
    /// made. Only the folder goes to stdout: Claude Code takes its last line as the path.
    /// </summary>
    int WorktreeCreate(HookInput input, CancellationToken cancellation)
    {
        var name = input.Name is { Length: > 0 } n ? n : throw new InvalidOperationException("the hook's input has no worktree name");
        var branch = BranchFor(name);
        var records = cli.Workspaces.List();
        if (Here.TryFind(input.Cwd, records, new StoredDefinitions(cli.Paths).Repos()) is not { } here)
        {
            Console.WriteLine(PlainWorktree(input.Cwd, name, branch));
            return 0;
        }

        var loader = new OrchestrationLoader(cli.Definitions, cli.Orchestrations, new StoredDefinitions(cli.Paths));
        var path = cli.Orchestrations.Resolve(here.Id) ?? throw new InvalidOperationException($"no repo or orchestration named '{here.Id}'");
        var loaded = loader.Load(path);
        if (loaded.Untrusted.Count > 0)
            throw new InvalidOperationException($"{string.Join(", ", loaded.Untrusted.Select(u => u.Path))} isn't approved to run. Read it, then: sprig trust add \"{loaded.Untrusted[0].Path}\"");
        if (!loaded.Complete)
            throw new InvalidOperationException($"{here.Id}'s definitions have problems: {string.Join("; ", loaded.Problems.Select(p => p.Message))}. See: sprig check");

        var progress = new SyncProgress<WorkspaceEvent>(e =>
        {
            if (e.State is StepState.Done or StepState.Failed) Console.Error.WriteLine($"sprig: {e.Step}{(e.Detail is { } d ? $": {d}" : "")}");
        });
        var record = cli.Workspaces.CheckOut(loaded, branch, progress: progress, cancellation: cancellation);
        record = record with { MadeForAgent = input.SessionId ?? "unknown" };
        new WorkspaceStore(cli.Paths).Save(record);
        Console.Error.WriteLine($"sprig: {record.SlotName} is on {branch}, ready; start it with: sprig start");
        Console.WriteLine(WorktreeOf(record, input.Cwd, records));
        return 0;
    }

    /// <summary>The new slot's worktree of the repo <paramref name="cwd"/> is in (in the repo's own checkout or another
    /// slot's), else the slot's folder.</summary>
    static string WorktreeOf(WorkspaceRecord record, string cwd, IEnumerable<WorkspaceRecord> before)
    {
        var repoPath = before.Append(record)
            .SelectMany(r => r.Repos)
            .Where(r => Within(cwd, r.Dir) || Within(cwd, r.Source))
            .OrderByDescending(r => Within(cwd, r.Dir) ? r.Dir.Length : r.Source.Length)
            .Select(r => r.RepoPath)
            .FirstOrDefault();
        return record.Repos.FirstOrDefault(r => r.RepoPath == repoPath)?.Dir
            ?? record.Folder
            ?? record.Repos.First().Dir;
    }

    /// <summary>A worktree where Claude Code puts its own, on a new branch from the repo's default.</summary>
    static string PlainWorktree(string cwd, string name, string branch)
    {
        if (!Git.IsRepo(cwd)) throw new InvalidOperationException($"{cwd} isn't in a git repo, so it can't have a worktree");
        var top = Git.MainCheckout(cwd) ?? Git.TopLevel(cwd);
        var dir = Path.Combine(top, ".claude", "worktrees", name);
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Git.AddWorktree(top, dir, branch, Git.DefaultBranch(top) ?? "HEAD");
        Console.Error.WriteLine($"sprig: {top} isn't a repo sprig knows, so this is a plain worktree");
        return Path.GetFullPath(dir);
    }

    /// <summary>
    /// A slot's worktree is freed, keeping its folder for the next branch, which is the point of a slot; a plain worktree
    /// is removed, with its branch when no other branch or remote has its commits.
    /// </summary>
    int WorktreeRemove(HookInput input, CancellationToken cancellation)
    {
        var path = input.WorktreePath is { Length: > 0 } p ? Path.GetFullPath(p) : throw new InvalidOperationException("the hook's input has no worktree_path");
        var slot = cli.Workspaces.List().FirstOrDefault(r => !r.IsMain && r.Repos.Any(x => x.Worktree && Same(x.Dir, path)) || r.Folder is { } f && Same(f, path));
        if (slot is not null)
        {
            if (!slot.IsFree) cli.Workspaces.Free(slot.Orchestration, slot.Name, cancellation: cancellation);
            Console.Error.WriteLine($"sprig: freed {slot.SlotName}; its folder stays for the next branch");
            return 0;
        }

        if (!Directory.Exists(path)) return 0;
        var top = Git.MainCheckout(path) ?? throw new InvalidOperationException($"{path} isn't a git worktree");
        var branch = Git.CurrentBranch(path);
        Git.RemoveWorktree(top, path, force: false);
        if (branch is not null && Git.CommitsOnlyOn(top, branch) == 0) Git.DeleteBranch(top, branch, force: true);
        return 0;
    }

    /// <summary>A few lines of context for the session: what this folder is to sprig and where its apps listen. Nothing
    /// outside a repo sprig knows, so a session elsewhere carries no noise.</summary>
    int SessionStart(HookInput input)
    {
        var records = cli.Workspaces.List();
        if (Here.TryFind(input.Cwd, records, new StoredDefinitions(cli.Paths).Repos()) is not { } here) return 0;
        var workspace = here.Workspace ?? WorkspaceRecord.MainName;
        var record = cli.Workspaces.Get(here.Id, workspace);
        var what = record is null || record.IsMain
            ? $"the main checkout of sprig's '{here.Id}' (it runs where it's checked out, on the repo's own ports)"
            : $"slot {record.SlotName} of sprig's '{here.Id}', on {record.Label}";
        Console.WriteLine($"sprig: this folder is {what}.");

        if (cli.Workspaces.LastPlan(here.Id, workspace) is { } plan)
        {
            var ports = plan.Ports.Where(x => x.Port is not null).Select(x => $"{x.Resource}{(x.Endpoint is "http" or "https" or "tcp" ? "" : $".{x.Endpoint}")} :{x.Port}").ToArray();
            if (ports.Length > 0) Console.WriteLine($"sprig: ports: {string.Join(", ", ports)}.");
        }
        var others = records.Count(r => r.Orchestration == here.Id && !r.IsMain && !r.IsFree && r.Name != workspace);
        if (others > 0) Console.WriteLine($"sprig: {others} other slot{(others == 1 ? "" : "s")} of it in use (sprig workspaces {here.Id} --json).");
        Console.WriteLine("sprig: drive it with --json; `sprig docs agent` says how. Its state now: sprig info --json.");
        return 0;
    }

    static bool Within(string path, string dir)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(dir), Path.GetFullPath(path));
        return relative == "." || !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
