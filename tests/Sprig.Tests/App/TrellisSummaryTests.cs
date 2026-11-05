using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Trellises;
using Sprig.Model;

namespace Sprig.Tests.App;

public sealed class TrellisSummaryTests : IDisposable
{
    readonly TempStore _store = new();
    readonly Dictionary<string, RepoModel> _repos = [];
    readonly Dictionary<string, string> _dirs = [];
    readonly TrellisBuilder _trellis = Trellis.Describe("storefront");

    public void Dispose() => _store.Dispose();

    RepoRef Add(string path, Action<RepoBuilder> describe)
    {
        var repo = Repo.Describe(path);
        describe(repo);
        _repos[path] = repo.BuildRepo();
        _dirs[path] = Path.Combine(_store.Root, path);
        return _trellis.Repo(path);
    }

    (LoadedTrellis loaded, Plan plan) Build()
    {
        var model = _trellis.BuildTrellis();
        var loaded = new LoadedTrellis(Path.Combine(_store.Root, "storefront.trellis.cs"), model, _repos, [], [], [], _dirs);
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
        var mail = _trellis.Mailpit("mail");
        _trellis.Bind("mail-smtp", mail.SmtpUrl);
        _trellis.Worktrees(from: "main");
        _trellis.Group("web", web, services);
        _trellis.Group("api", services.Only("catalog-api"));
    }

    [Fact]
    public void Cards_say_what_each_repo_runs_and_what_fills_each_need()
    {
        Shop();
        var (loaded, plan) = Build();

        var cards = TrellisSummary.Cards(loaded, plan, new StoredDefinitions(_store.Paths));

        var services = cards.Single(c => c.Name == "shop-services");
        Assert.Equal("catalog-api, orders-api, then migrate first", services.Runs);
        Assert.Equal("pg", services.Infra);
        Assert.Equal("catalog-url", services.Offers);
        Assert.Equal([new NeedRow("mail-smtp", "mail, added by the trellis", true)], services.Needs);
        var web = cards.Single(c => c.Name == "shop-web");
        Assert.Equal([new NeedRow("catalog-url", "shop-services", true), new NeedRow("payments-url", "nothing fills it", false)], web.Needs);
        Assert.Equal("Runs shop-services and shop-web together.", TrellisSummary.Description(loaded));
    }

    [Fact]
    public void The_trellis_says_what_it_adds_and_how_its_sprouts_run()
    {
        Shop();
        var (_, plan) = Build();

        Assert.Equal([new AddedRow("mail", "mailpit, one per sprout. Fills mail-smtp.")], TrellisSummary.Added(plan));
        var policies = TrellisSummary.Policies(plan).ToDictionary(p => p.Name, p => p.What);
        Assert.Equal("Each sprout gets its own, on a new branch from main", policies["Worktrees"]);
        Assert.StartsWith("Stable", policies["Ports"]);
        // A repo whose every app is in a group is named once.
        Assert.Equal("web: shop-web, shop-services\napi: catalog-api", policies["Groups"]);
    }

    [Fact]
    public void A_repo_that_has_not_run_says_why_on_its_card()
    {
        _trellis.UseRepo("nope");
        var (loaded, _) = Build();

        var card = Assert.Single(TrellisSummary.Cards(loaded, null, new StoredDefinitions(_store.Paths)));

        Assert.False(card.HasModel);
        Assert.Equal("'nope' isn't on the Repos list", card.Problem);
        Assert.Equal("Runs nope on its own.", TrellisSummary.Description(loaded));
    }
}

public class ReposViewModelTests
{
    [Fact]
    public async Task A_repo_not_yet_approved_shows_it_and_switching_where_it_is_kept_copies_it_into_sprig_unapproved()
    {
        using var store = new TempStore();
        var folder = Path.Combine(store.Root, "src", "shop-web");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sprig.cs"), "// shop-web's own, never approved");
        var services = new AppServices(store.Root);
        services.Authoring.AddRepo(folder, "shop-web");

        var vm = new ReposViewModel(services);
        await WaitFor(() => vm.NeedsApproval);

        var item = Assert.Single(vm.Repos);
        Assert.Equal(item, vm.Selected);
        Assert.True(item.InRepo);
        Assert.False(vm.HasModel);
        Assert.Contains("Not approved yet", vm.Check);

        await vm.SetKeptCommand.ExecuteAsync("sprig");
        await WaitFor(() => vm.NeedsApproval);

        Assert.True(vm.InSprig);
        Assert.Equal("in sprig", vm.Selected!.Kept);
        Assert.Equal("// shop-web's own, never approved", File.ReadAllText(services.Stored.CopyFile("shop-web")));
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
