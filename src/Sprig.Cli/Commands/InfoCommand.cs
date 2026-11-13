using System.ComponentModel;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Show a workspace whole: where it is, its apps and infra and their state, and its inputs")]
public sealed class InfoCommand(CliContext cli) : Command<InfoCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--trust")]
        [Description("Approve and run any definition that isn't approved yet, without asking")]
        public bool Trust { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace);
        var plan = record is null ? null : cli.Workspaces.LastPlan(record.Orchestration, record.Name);
        var runners = record is null ? [] : (cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [])
            .Where(r => r.Workspace == record.Key).ToArray();
        if (SettingsCommand.Read(cli, s.Orchestration, s.Workspace, new TrustingSettings { Trust = s.Trust, Json = s.Json, UnmaskSecrets = s.UnmaskSecrets }) is not { } settings)
            return 1;

        if (s.Json)
        {
            CliOutput.Json(new
            {
                ok = true, workspace = record is null ? null : Shown.Record(record, plan, s), plan = Shown.Plan(plan, s), runners,
                settings = settings.Rows, secretsUnmasked = s.UnmaskSecrets,
            });
            return 0;
        }

        var id = settings.Loaded.Id;
        if (record is null)
        {
            cli.Ansi.MarkupLine($"[bold]{Markup.Escape(id)}[/] / [bold]{Markup.Escape(s.Workspace)}[/]  [dim]not made yet[/]");
            cli.Ansi.MarkupLine(s.Workspace == WorkspaceRecord.MainName
                ? $"[dim]Start it with: sprig start {Markup.Escape(id)}[/]"
                : $"[dim]Check a branch out with: sprig up {Markup.Escape(id)} -b <branch>[/]");
        }
        else Header(record, runners);
        cli.Ansi.WriteLine();

        if (record is not null && plan is not null)
        {
            Apps(record, plan, runners);
            Infra(record, plan);
        }
        SettingsCommand.Write(cli, id, settings.Rows, "[bold]Inputs[/]");
        return 0;
    }

    void Header(WorkspaceRecord record, IReadOnlyList<RunnerStatus> runners)
    {
        var state = WorkspacesCommand.RunState(record, runners, cli.Workspaces.RunningProjects());
        cli.Ansi.MarkupLine(record.IsMain
            ? $"[bold]{Markup.Escape(record.Orchestration)}[/]  {Markup.Escape(record.CheckoutBranch ?? "")}  {state}  [dim](main checkout)[/]"
            : record.InPlace
            ? $"[bold]{Markup.Escape(record.Orchestration)}[/] / [bold]{Markup.Escape(record.Name)}[/]  {state}  [dim](repos in place)[/]"
            : $"[bold]{Markup.Escape(record.SlotName)}[/]  {Markup.Escape(record.Branch ?? "no branch")}  {state}");
        var folder = record.Folder ?? string.Join(", ", record.Repos.Select(r => r.Dir));
        cli.Ansi.MarkupLine($"[dim]Folder[/]   {Markup.Escape(folder.Replace('\\', '/'))}");
        if (record.LastUpAt is { } at) cli.Ansi.MarkupLine($"[dim]Ready since[/]  {Markup.Escape(at.LocalDateTime.ToString("g"))}");
    }

    void Apps(WorkspaceRecord record, Sprig.Engine.Planning.Plan plan, IReadOnlyList<RunnerStatus> runners)
    {
        if (plan.Apps.Count == 0) return;
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Apps[/]").AddColumns("App", "State", "Url", "Folder");
        foreach (var app in plan.Apps)
        {
            var name = $"{app.Owner}/{app.Name}";
            var runner = runners.FirstOrDefault(r => r.Name == name);
            var repo = record.Repos.FirstOrDefault(r => r.RepoPath == app.RepoPath);
            var dir = repo is null ? "" : Path.GetFullPath(Path.Combine(repo.Dir, app.Path)).Replace('\\', '/');
            var url = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name) is { Port: { } port } ? $"http://localhost:{port}" : "";
            table.AddRow(Markup.Escape(name), runner is null ? "[dim]not started[/]" : PsCommand.State(runner), Markup.Escape(url), Markup.Escape(dir));
        }
        cli.Ansi.Write(table);
    }

    void Infra(WorkspaceRecord record, Sprig.Engine.Planning.Plan plan)
    {
        if (plan.Containers.Count + plan.Compose.Count == 0) return;
        var states = record.IsMain ? null : cli.Workspaces.InfraStates(cli.Workspaces.InfraTargets(record, plan));
        var own = record.IsMain ? cli.Workspaces.OwnInfraOf(plan) : [];
        string State(string key) => record.IsMain
            ? own.FirstOrDefault(o => o.Key == key)?.Answering switch { true => "[green]yours, listening[/]", false => "[red]yours, not listening[/]", _ => "[dim]yours[/]" }
            : states is null ? "[dim]docker isn't answering[/]"
            : states.GetValueOrDefault(key) switch
            {
                { State: "running" } => "[green]running[/]",
                { State: var other } => $"[dim]{Markup.Escape(other)}[/]",
                null => "[dim]not started[/]",
            };
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Infra[/]").AddColumns("Infra", "State", "Ports");
        foreach (var c in plan.Containers)
            table.AddRow(Markup.Escape($"{c.Owner}/{c.Name}"), State($"{c.Owner}/{c.Name}"), Markup.Escape(string.Join(", ", c.Ports)));
        foreach (var c in plan.Compose)
            foreach (var svc in c.Services)
                table.AddRow(Markup.Escape($"{c.Owner}/{svc.Name}"), State($"{c.Owner}/{svc.Name}"), Markup.Escape(string.Join(", ", svc.Mappings)));
        cli.Ansi.Write(table);
    }
}
