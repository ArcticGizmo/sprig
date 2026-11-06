using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Orchestrations;
using Sprig.Model;

namespace Sprig.Tests.App;

public sealed class OrchestrationSummaryTests : IDisposable
{
    readonly TempStore _store = new();
    readonly Dictionary<string, RepoModel> _repos = [];
    readonly Dictionary<string, string> _dirs = [];
    readonly OrchestrationBuilder _orchestration = Orchestration.Describe("storefront");

    public void Dispose() => _store.Dispose();

    RepoRef Add(string path, Action<RepoBuilder> describe)
    {
        var repo = Repo.Describe(path);
        describe(repo);
        _repos[path] = repo.BuildRepo();
        _dirs[path] = Path.Combine(_store.Root, path);
        return _orchestration.Repo(path);
    }

    (LoadedOrchestration loaded, Plan plan) Build()
    {
        var model = _orchestration.BuildOrchestration();
        var loaded = new LoadedOrchestration(Path.Combine(_store.Root, "storefront.orchestration.cs"), model, _repos, [], [], [], _dirs);
        var plan = Planner.Build(new PlanInput(model, _repos, "plan", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
        return (loaded, plan);
    }

    void Shop()
    {
        var services = Add("shop-services", repo =>
        {
            var pg = repo.Postgres("pg");
            var migrate = repo.DotNetJob("migrate", "tools/Migrate").WaitFor(pg);
            var catalog = repo.DotNet("catalog-api", ".").Http().WaitFor(pg, migrate);
            repo.DotNet("orders-api", ".").Http().Env("Smtp__Url", repo.Import<Url>("mail-smtp"));
            repo.Export<Url>("catalog-url", catalog.Url);
        });
        var web = Add("shop-web", repo => repo.Node("web", ".").Http()
            .Env("API", repo.Import<Url>("catalog-url"))
            .Env("PAY", repo.Import<Url>("payments-url")));
        var mail = _orchestration.Mailpit("mail");
        _orchestration.Bind("mail-smtp", mail.SmtpUrl);
        _orchestration.Group("web", web, services);
        _orchestration.Group("api", services.Only("catalog-api"));
    }

    [Fact]
    public void Cards_say_what_each_repo_runs_and_what_fills_each_need()
    {
        Shop();
        var (loaded, plan) = Build();

        var cards = OrchestrationSummary.Cards(loaded, plan, new StoredDefinitions(_store.Paths));

        var services = cards.Single(c => c.Name == "shop-services");
        Assert.Equal("catalog-api, orders-api, then migrate first", services.Runs);
        Assert.Equal("pg", services.Infra);
        Assert.Equal("catalog-url", services.Offers);
        Assert.Equal([new NeedRow("mail-smtp", "mail, added by the orchestration", true)], services.Needs);
        var web = cards.Single(c => c.Name == "shop-web");
        Assert.Equal([new NeedRow("catalog-url", "shop-services", true), new NeedRow("payments-url", "nothing fills it", false)], web.Needs);
        Assert.Equal("Runs shop-services and shop-web together.", OrchestrationSummary.Description(loaded));
    }

    [Fact]
    public void The_orchestration_says_what_it_adds_and_its_groups()
    {
        Shop();
        var (_, plan) = Build();

        Assert.Equal([new AddedRow("mail", "mailpit, one per workspace. Fills mail-smtp.")], OrchestrationSummary.Added(plan));
        // A repo whose every app is in a group is named once.
        Assert.Equal([new PolicyRow("web", "shop-web, shop-services"), new PolicyRow("api", "catalog-api")], OrchestrationSummary.Policies(plan));
    }

    [Fact]
    public void The_picture_puts_what_the_orchestration_adds_first_and_each_repo_right_of_what_it_takes_from()
    {
        Shop();
        var (_, plan) = Build();

        var graph = OrchestrationGraph.Build(plan, new Dictionary<string, string> { ["shop-web"] = "in sprig" });

        var x = graph.Boxes.ToDictionary(b => b.Owner, b => b.X);
        Assert.True(x["storefront"] < x["shop-services"]);
        Assert.True(x["shop-services"] < x["shop-web"]);
        Assert.True(graph.Boxes.Single(b => b.Owner == "storefront").IsOrchestration);
        Assert.True(graph.Boxes.Single(b => b.Owner == "shop-web").InSprig);
        Assert.Equal("pg", graph.Boxes.Single(b => b.Owner == "shop-services").Infra);
        Assert.Equal(["catalog-api", "orders-api", "migrate"], graph.Boxes.Single(b => b.Owner == "shop-services").Items.Select(i => i.Name));
    }

    [Fact]
    public void Wires_join_what_offers_a_value_to_the_app_that_needs_it_and_a_need_nothing_fills_is_a_stub()
    {
        Shop();
        var (_, plan) = Build();
        var graph = OrchestrationGraph.Build(plan);

        var lines = graph.Lines(null, null);

        var mail = lines.Single(l => l.Label == "mail-smtp");
        Assert.Equal(("shop-services/orders-api", "storefront/mail"), (mail.ConsumerKey, mail.ProviderKey));
        Assert.True(mail.X1 < mail.X2);
        var catalog = lines.Single(l => l.Label == "catalog-url");
        Assert.Equal(("shop-web/web", "shop-services/catalog-api"), (catalog.ConsumerKey, catalog.ProviderKey));
        var stub = lines.Single(l => !l.Filled);
        Assert.Equal("nothing fills payments-url", stub.Label);
        Assert.Equal("shop-web/web", stub.ConsumerKey);
    }

    [Fact]
    public void Picking_an_app_lights_its_wires_and_showing_a_group_dims_what_it_does_not_start()
    {
        Shop();
        var (_, plan) = Build();
        var graph = OrchestrationGraph.Build(plan);
        var items = graph.Boxes.SelectMany(b => b.Items).ToDictionary(i => i.Key);

        var lines = graph.Lines("shop-services/catalog-api", plan.Groups.Single(g => g.Name == "api").Apps.ToHashSet());

        Assert.True(items["shop-services/catalog-api"].IsSelected);
        Assert.True(lines.Single(l => l.Label == "catalog-url").Highlighted);
        Assert.False(lines.Single(l => l.Label == "mail-smtp").Highlighted);
        Assert.True(items["shop-web/web"].IsDimmed);
        Assert.True(items["shop-services/orders-api"].IsDimmed);
        Assert.False(items["shop-services/catalog-api"].IsDimmed);
        // What the orchestration adds starts with any group.
        Assert.False(items["storefront/mail"].IsDimmed);
        Assert.True(lines.Single(l => l.Label == "mail-smtp").Dimmed);
    }

    [Fact]
    public void A_need_several_repos_offer_is_unfilled_and_one_the_repo_fills_itself_says_so()
    {
        Add("shop-services", repo =>
        {
            repo.DotNet("orders-api", ".").Http().Env("Smtp__Url", repo.Import<Url>("mail-smtp").OrElse(repo.Mailpit("mail").SmtpUrl));
            repo.Export<Url>("catalog-url", "http://a");
        });
        Add("shop-legacy", repo => repo.Export<Url>("catalog-url", "http://b"));
        Add("shop-web", repo => repo.Node("web", ".").Http().Env("API", repo.Import<Url>("catalog-url")));
        var (loaded, plan) = Build();

        var cards = OrchestrationSummary.Cards(loaded, plan, new StoredDefinitions(_store.Paths));

        Assert.Equal([new NeedRow("mail-smtp", "its own mail", true)], cards.Single(c => c.Name == "shop-services").Needs);
        Assert.Equal([new NeedRow("catalog-url", "shop-services and shop-legacy all offer it", false)], cards.Single(c => c.Name == "shop-web").Needs);
        var (repo, need) = Assert.Single(OrchestrationSummary.Unfilled(loaded, cards));
        Assert.Equal(("shop-web", "catalog-url", "url"), (repo, need.Import, need.Type));
        Assert.Equal(["shop-services", "shop-legacy"], need.Offers);
    }

    [Fact]
    public void A_repo_that_has_not_run_says_why_on_its_card()
    {
        _orchestration.UseRepo("nope");
        var (loaded, _) = Build();

        var card = Assert.Single(OrchestrationSummary.Cards(loaded, null, new StoredDefinitions(_store.Paths)));

        Assert.False(card.HasModel);
        Assert.Equal("'nope' isn't on the Repos list", card.Problem);
        Assert.Equal("Runs nope on its own.", OrchestrationSummary.Description(loaded));
    }
}

public class OrchestrationChecklistTests
{
    static RepoCard Card(string name, string? problem = null, params NeedRow[] needs)
        => new(name, name, null, true, "", "web", "none", "nothing", needs, problem);

    static ChecklistState[] States(IReadOnlyList<ChecklistStep> steps) => steps.Select(s => s.State).ToArray();

    [Fact]
    public void Unapproved_code_is_the_first_step_and_everything_after_it_waits()
    {
        var steps = OrchestrationChecklist.Steps(pendingApprovals: 2, loadProblems: 0, cards: [], planned: false, otherErrors: 0);

        Assert.Equal([ChecklistState.Open, ChecklistState.Waiting, ChecklistState.Waiting, ChecklistState.Waiting, ChecklistState.Waiting], States(steps));
        Assert.Equal(StepAction.Review, steps[0].Action);
        Assert.Equal("Review 2 files", steps[0].ActionLabel);
        Assert.Contains("2 files to read and approve", steps[0].Detail);
        Assert.True(OrchestrationChecklist.Blocks(steps));
    }

    [Fact]
    public void A_need_nothing_fills_is_its_own_step_naming_the_repo_and_why_with_a_button_that_fills_it()
    {
        var steps = OrchestrationChecklist.Steps(0, 0, [Card("shop-services", null, new NeedRow("mail-smtp", "nothing fills it", false))], planned: true, otherErrors: 0);

        Assert.Equal([ChecklistState.Done, ChecklistState.Done, ChecklistState.Open, ChecklistState.Waiting, ChecklistState.Waiting], States(steps));
        Assert.StartsWith("mail-smtp (for shop-services): nothing fills it.", steps[2].Detail);
        Assert.Equal((StepAction.FillNeeds, "Fill it…"), (steps[2].Action, steps[2].ActionLabel));
    }

    [Fact]
    public void A_repo_that_did_not_run_says_why_and_other_plan_errors_point_at_Values()
    {
        var notRun = OrchestrationChecklist.Steps(0, 1, [Card("shop-web", "Its definition doesn't run; see the problems above")], planned: false, otherErrors: 0);
        Assert.Equal(ChecklistState.Open, notRun[1].State);
        Assert.StartsWith("shop-web: Its definition doesn't run", notRun[1].Detail);

        var errors = OrchestrationChecklist.Steps(0, 0, [Card("shop-web")], planned: true, otherErrors: 3);
        Assert.Equal(ChecklistState.Open, errors[3].State);
        Assert.Equal(StepAction.ShowValues, errors[3].Action);
    }

    [Fact]
    public void Once_nothing_is_in_the_way_only_starting_is_left_and_the_checklist_steps_aside()
    {
        var steps = OrchestrationChecklist.Steps(0, 0, [Card("shop-web")], planned: true, otherErrors: 0);

        Assert.Equal([ChecklistState.Done, ChecklistState.Done, ChecklistState.Done, ChecklistState.Done, ChecklistState.Open], States(steps));
        Assert.False(OrchestrationChecklist.Blocks(steps));
    }
}

public class ReposViewModelTests
{
    [Fact]
    public async Task A_repo_not_yet_approved_shows_it_and_copying_it_into_sprig_adds_a_new_unapproved_entry()
    {
        using var store = new TempStore();
        var folder = Path.Combine(store.Root, "src", "shop-web");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sprig.cs"), "// shop-web's own, never approved");
        var services = new AppServices(store.Root);
        services.Authoring.AddRepo(folder, "shop-web");

        var vm = new ReposViewModel(services);
        Assert.Empty(vm.Repos);
        Assert.Equal(1, vm.NavCount);
        vm.IsActive = true;
        await WaitFor(() => vm.NeedsApproval);

        var item = Assert.Single(vm.Repos);
        Assert.Equal(item, vm.Selected);
        Assert.True(item.InRepo);
        Assert.False(vm.HasModel);
        Assert.Contains("Not approved yet", vm.Check);

        Assert.Equal("Copy into sprig…", vm.DeriveLabel);
        vm.OpenDeriveCommand.Execute(null);
        Assert.True(vm.IsDeriveOpen);
        Assert.False(vm.DeriveBlocked);
        Assert.Equal("shop-web-sprig", vm.DeriveId);
        await vm.DeriveCommand.ExecuteAsync(null);
        await WaitFor(() => vm.Selected?.Id == "shop-web-sprig" && vm.NeedsApproval);

        Assert.False(vm.IsDeriveOpen);
        Assert.Equal(2, vm.Repos.Count);
        Assert.Equal("in sprig", vm.Selected!.Kept);
        Assert.Equal("Write to the repo…", vm.DeriveLabel);
        Assert.Equal("// shop-web's own, never approved", File.ReadAllText(services.Stored.CopyFile("shop-web-sprig")));
        Assert.True(vm.Repos.Single(r => r.Id == "shop-web").InRepo);

        // The folder has its own sprig.cs, so writing the copy back is refused before anything is asked.
        vm.OpenDeriveCommand.Execute(null);
        Assert.True(vm.DeriveBlocked);
        Assert.Contains("'shop-web'", vm.DeriveMessage);
    }

    [Fact]
    public async Task Add_asks_which_kind_first_and_each_kind_makes_that_kind_of_entry()
    {
        using var store = new TempStore();
        var web = Path.Combine(store.Root, "src", "shop-web");
        var mobile = Path.Combine(store.Root, "src", "shop-mobile");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(mobile);
        File.WriteAllText(Path.Combine(web, "sprig.cs"), "// shop-web's own");
        File.WriteAllText(Path.Combine(mobile, "package.json"), """{ "name": "mobile", "scripts": { "dev": "vite" } }""");
        var services = new AppServices(store.Root);
        var vm = new ReposViewModel(services);

        vm.OpenChooseCommand.Execute(null);
        Assert.True(vm.IsChooseOpen);
        Assert.False(vm.IsAddOpen);

        // "It has a sprig.cs" refuses a folder without one, before anything is added.
        vm.OpenAddInRepoCommand.Execute(null);
        Assert.False(vm.IsChooseOpen);
        Assert.Equal("Use the repo's sprig.cs", vm.AddTitle);
        vm.AddFolder = mobile;
        Assert.Equal("shop-mobile", vm.AddId);
        await vm.AddCommand.ExecuteAsync(null);
        Assert.True(vm.AddFailed);
        Assert.Contains("no sprig.cs", vm.AddMessage);
        Assert.Empty(services.Stored.Repos());

        vm.AddFolder = web;
        vm.AddId = "shop-web";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.False(vm.IsAddOpen);
        Assert.True(services.Stored.Repo("shop-web")!.InRepo);

        // "Write one in sprig" over a folder with its own sprig.cs starts from a copy of it, and opens it.
        vm.BackToChooseCommand.Execute(null);
        vm.OpenAddInSprigCommand.Execute(null);
        Assert.Equal("Write one in sprig", vm.AddTitle);
        vm.AddFolder = web;
        Assert.Equal("shop-web-sprig", vm.AddId);
        await vm.AddCommand.ExecuteAsync(null);
        var copy = services.Stored.Repo("shop-web-sprig")!;
        Assert.False(copy.InRepo);
        Assert.Equal("// shop-web's own", File.ReadAllText(copy.DefinitionFile));
        Assert.True(vm.Editor.IsEditing);
    }

    [Theory]
    [InlineData("{catalog-api.http.url}", "catalog-api's url")]
    [InlineData("http://localhost:{web.http.port}/cart", "http://localhost:web's port/cart")]
    [InlineData("{import.mail-smtp}", "its import mail-smtp")]
    [InlineData("Host=x;Password=hunter2", "Host=x;Password=***")]
    public void Offers_are_described_in_words(string template, string expected) => Assert.Equal(expected, ReposViewModel.Describe(template));

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
