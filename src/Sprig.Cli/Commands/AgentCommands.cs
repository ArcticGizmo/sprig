using System.ComponentModel;
using System.Reflection;
using Sprig.Engine.Definitions;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Model;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Print the JSON Schema for a repo's sprig.yaml or an orchestration file, as IDEs and agents check a definition with")]
public sealed class SchemaCommand : Command<SchemaCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[kind]")]
        [Description("repo (default) or orchestration")]
        public string Kind { get; set; } = "repo";
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => s.Kind is "repo" or "orchestration" ? ValidationResult.Success() : ValidationResult.Error("the kind is repo or orchestration");

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        Console.Write(s.Kind == "repo" ? DefinitionSchema.Repo() : DefinitionSchema.Orchestration());
        return 0;
    }
}

[Description("Print sprig's own docs, for the version installed: the definition format, or a guide for agents")]
public sealed class DocsCommand : Command<DocsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[topic]")]
        [Description("format (default): how a sprig.yaml is written; agent: how an agent makes a repo ready and runs workspaces")]
        public string Topic { get; set; } = "format";
    }

    internal static readonly string[] Topics = ["format", "agent"];

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => Topics.Contains(s.Topic) ? ValidationResult.Success() : ValidationResult.Error($"the topic is one of {string.Join(", ", Topics)}");

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        Console.Write(Text(s.Topic));
        return 0;
    }

    internal static string Text(string topic)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"docs.{topic}.md")
            ?? throw new InvalidOperationException($"this build has no '{topic}' docs");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

[Description("Check a definition file without registering or approving it: it's read and planned, so its references, ports and files are checked too, and nothing runs")]
public sealed class CheckCommand(CliContext cli) : Command<CheckCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[file]")]
        [Description("A sprig.yaml or an orchestration file, or a folder with a sprig.yaml (default: this folder's)")]
        public string? File { get; set; }
    }

    /// <summary>What a workspace planned only to check is called; it's never made.</summary>
    const string CheckWorkspace = "check";

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var path = LongPath.Of(s.File ?? Environment.CurrentDirectory);
        if (Directory.Exists(path)) path = Path.Combine(path, OrchestrationLoader.RepoDefinitionFile);
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException($"no definition at {path}. Draft one with: sprig init");

        var (kind, problems, asks) = Check(path);
        var errors = problems.Count(p => p.Severity == ProblemSeverity.Error);
        if (s.Json)
        {
            CliOutput.Json(new
            {
                ok = errors == 0,
                code = errors == 0 ? null : CliErrors.Problems,
                file = path,
                kind,
                problems,
                asks = asks.Select(a => new { name = a.Name, type = a.Type, owner = a.Owner }),
            });
        }
        else
        {
            cli.Ansi.MarkupLine($"[bold]{Markup.Escape(path)}[/] [dim]({kind ?? "unreadable"})[/]");
            if (asks.Count > 0)
                cli.Ansi.MarkupLine($"[dim]Asked for when a workspace is made: {Markup.Escape(string.Join(", ", asks.Select(a => a.Name)))}[/]");
            PlanPrinter.Problems(cli.Ansi, problems, Path.GetDirectoryName(path)!);
        }
        return errors == 0 ? 0 : 1;
    }

    (string? Kind, IReadOnlyList<PlanProblem> Problems, IReadOnlyList<PlannedAsk> Asks) Check(string path)
    {
        var read = DefinitionReader.Read(System.IO.File.ReadAllText(path), path);
        var loaded = read.Model switch
        {
            RepoModel repo => Alone(path, repo),
            OrchestrationModel => new OrchestrationLoader(new DefinitionRunner(new ReadingOnly(), cli.Paths), cli.Orchestrations, new StoredDefinitions(cli.Paths)).Load(path),
            _ => null,
        };
        var kind = read.Model switch { RepoModel => "repo", OrchestrationModel => "orchestration", _ => null };
        if (loaded?.Orchestration is null || loaded.Problems.Count > 0) return (kind, [.. read.Problems, .. loaded?.Problems ?? []], []);

        var problems = new List<PlanProblem>(read.Problems);
        var compose = loaded.ComposeFiles(problems);
        var plan = Planner.Build(new PlanInput(loaded.Orchestration, loaded.Repos, CheckWorkspace, cli.Settings.Get(), PortAllocator.Listening(),
            ComposeFiles: compose));
        if (!plan.HasErrors) problems.AddRange(cli.Workspaces.CheckFiles(plan, loaded.ReadFrom));
        return (kind, [.. problems, .. plan.Problems.Where(p => !problems.Contains(p))], plan.ToAsk);
    }

    /// <summary>A repo file planned on its own, as sprig plans a repo from the Repos list, but straight from the file.</summary>
    static LoadedOrchestration Alone(string path, RepoModel repo)
    {
        var at = new SourceLocation(path, 0);
        var orchestration = new OrchestrationModel(DefinitionReader.CurrentSchema, $"{repo.Name}, on its own", at, Containers: [],
            Repos: [new RepoRefModel(repo.Name, at)], Bindings: [], Groups: []);
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal) { [repo.Name] = Path.GetDirectoryName(path)! };
        return new LoadedOrchestration(path, orchestration, new Dictionary<string, RepoModel>(StringComparer.Ordinal) { [repo.Name] = repo },
            [], [], [], dirs);
    }

    /// <summary>Approval guards running a definition; checking only reads it, so every file counts as approved here and
    /// nothing is recorded.</summary>
    sealed class ReadingOnly : ITrustStore
    {
        public TrustDecision Check(string definitionPath, string sha256) => new(TrustState.Trusted, sha256);
        public void Approve(string definitionPath, string sha256) { }
        public void Revoke(string definitionPath) { }
        public IReadOnlyList<TrustEntry> List() => [];
    }
}
