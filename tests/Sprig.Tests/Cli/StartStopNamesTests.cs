using Sprig.Cli.Commands;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Workspaces;
using Sprig.Model;

namespace Sprig.Tests.Cli;

/// <summary><c>sprig start</c> and <c>sprig stop</c> take apps and infra alike, by name.</summary>
public class StartStopNamesTests
{
    static (WorkspaceRecord, Plan) Shop()
    {
        var orchestration = Yaml.Orchestration("""
            schema: 1
            orchestration: storefront
            repos:
              shop-services: { path: shop-services }
              shop-web: { path: shop-web }
            containers:
              mail: { preset: mailpit }
            """);
        var services = Yaml.Repo("""
            schema: 1
            repo: shop-services
            containers:
              db: { preset: postgres }
            compose:
              docker-compose.yml:
                services:
                  search:
                    ports: { http: { container: 7700 } }
            apps:
              api:
                run: dotnet
                path: src/Api
                ports: { http: any }
            """);
        var web = Yaml.Repo("""
            schema: 1
            repo: shop-web
            containers:
              db: { preset: postgres }
            apps:
              web:
                run: node
                ports: { http: any }
            """);
        var plan = Planner.Build(new PlanInput(orchestration, new Dictionary<string, RepoModel> { ["shop-services"] = services, ["shop-web"] = web },
            "1", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
        var now = DateTimeOffset.Now;
        return (new WorkspaceRecord("storefront", "storefront.orchestration.yaml", "1", "feat-a", "stable", [], [], [], WorkspaceState.Up, now, now), plan);
    }

    [Fact]
    public void Names_are_sorted_into_apps_and_infra_by_their_keys()
    {
        var (record, plan) = Shop();

        var (apps, infra) = AppsSettings.Split(record, plan, ["api", "shop-web/web", "search", "mail", "shop-web/db"]);

        Assert.Equal(["api", "shop-web/web"], apps);
        Assert.Equal(["shop-services/search", "storefront/mail", "shop-web/db"], infra);
    }

    [Fact]
    public void A_name_in_two_repos_or_in_none_is_refused_saying_what_there_is()
    {
        var (record, plan) = Shop();

        Assert.Contains("name it as <repo>/db", Assert.Throws<InvalidOperationException>(() => AppsSettings.Split(record, plan, ["db"])).Message);
        var none = Assert.Throws<InvalidOperationException>(() => AppsSettings.Split(record, plan, ["redis"])).Message;
        Assert.Contains("no app or infra 'redis'", none);
        Assert.Contains("shop-services/search", none);
    }
}
