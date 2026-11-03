using Sprig.App;
using Sprig.App.Controls;
using Sprig.App.ViewModels;
using Sprig.Engine.Planning;
using Sprig.Model;

namespace Sprig.Tests.App;

public class TrellisesViewModelTests
{
    [Fact]
    public async Task Adding_an_unapproved_trellis_asks_first_and_runs_nothing()
    {
        using var store = new TempStore();
        Directory.CreateDirectory(store.Root);
        var file = Path.Combine(store.Root, "storefront.trellis.cs");
        File.WriteAllText(file, "using Sprig;\nreturn Trellis.Describe(\"storefront\").Emit(args);");
        var vm = new TrellisesViewModel(new AppServices(store.Root)) { NewPath = file };

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.HasPendingTrust);
        var pending = Assert.Single(vm.PendingTrust);
        Assert.Equal(file.Replace('\\', '/'), pending.Path);
        Assert.False(pending.Changed);
        Assert.Empty(vm.Trellises);
    }

    [Fact]
    public async Task A_path_that_is_not_a_file_is_refused_with_a_reason()
    {
        using var store = new TempStore();
        var vm = new TrellisesViewModel(new AppServices(store.Root)) { NewPath = Path.Combine(store.Root, "nope.trellis.cs") };

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.AddFailed);
        Assert.Contains("Choose a trellis file", vm.AddMessage);
    }

    [Fact]
    public void The_plan_view_lists_wiring_between_repos_and_masks_passwords()
    {
        var at = new SourceLocation(@"C:\tb\shop-web\sprig.cs", 7);
        var plan = new Plan("storefront", "plan", "stable", "main",
            [new PlannedPort("shop-services", "catalog-api", "http", 8000, null, null)],
            [
                new PlannedApp("shop-web", "shop-web", "web", "node", ".",
                    [new PlannedEnv("VITE_CATALOG_URL", new PlannedValue("{import.catalog-url}", "http://localhost:8000",
                        ["import catalog-url ← shop-services", "catalog-api.http port 8000"]), ".env.local", at)],
                    null, [], []),
                new PlannedApp("shop-services", "shop-services", "catalog-api", "dotnet", "src/Catalog.Api",
                    [new PlannedEnv("Db", new PlannedValue("x", "Host=a;Password=hunter2", []), null, at)], null, [], []),
            ],
            [], [], [], [new PlanProblem(ProblemSeverity.Warning, "careful", at)]);

        var view = new PlanView(plan, @"C:\tb");

        var wire = Assert.Single(view.Wiring);
        Assert.Equal("shop-web / web", wire.Consumer);
        Assert.Equal("shop-services (catalog-url)", wire.Provider);
        Assert.Equal("Host=a;Password=***", view.Apps[1].Rows[0].Value);
        Assert.Equal("shop-web/sprig.cs:7", Assert.Single(view.Problems).Where);
        Assert.Equal("Sprout 'plan'  ·  stable ports  ·  worktrees from main", view.Summary);
    }
}

public class PathCompletionTests
{
    [Fact]
    public void Suggests_folders_first_with_a_trailing_slash_then_matching_files_with_forward_slashes()
    {
        using var store = new TempStore();
        var root = Path.Combine(store.Root, "tb");
        Directory.CreateDirectory(Path.Combine(root, "shop-web"));
        Directory.CreateDirectory(Path.Combine(root, "shop-services"));
        File.WriteAllText(Path.Combine(root, "storefront.trellis.cs"), "");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "");

        var typed = PathCompletion.ToPosix(root) + "/s";
        var all = PathCompletion.Suggest(typed);
        var trellises = PathCompletion.Suggest(typed, f => f.EndsWith(".trellis.cs"));

        var prefix = PathCompletion.ToPosix(root) + "/";
        Assert.Equal([prefix + "shop-services/", prefix + "shop-web/", prefix + "storefront.trellis.cs"], all);
        Assert.Contains(prefix + "storefront.trellis.cs", trellises);
        Assert.DoesNotContain(all, s => s.Contains('\\'));
    }

    [Fact]
    public void A_folder_that_does_not_exist_suggests_nothing()
        => Assert.Empty(PathCompletion.Suggest("Z:/definitely/not/here/x"));
}
