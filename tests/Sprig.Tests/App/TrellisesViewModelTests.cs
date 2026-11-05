using Sprig.App;
using Sprig.App.Controls;
using Sprig.App.ViewModels;
using Sprig.Engine.Planning;
using Sprig.Model;

namespace Sprig.Tests.App;

public class TrellisesViewModelTests
{
    [Fact]
    public async Task An_unapproved_trellis_is_listed_and_selected_by_its_file_name_but_runs_nothing_until_approved()
    {
        using var store = new TempStore();
        Directory.CreateDirectory(store.Root);
        var file = Path.Combine(store.Root, "storefront.trellis.cs");
        File.WriteAllText(file, "using Sprig;\nreturn Trellis.Describe(\"shop\").Emit(args);");
        var vm = new TrellisesViewModel(new AppServices(store.Root));
        vm.OpenAddCommand.Execute(null);
        vm.NewPath = file;

        await vm.AddCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddOpen);
        var item = Assert.Single(vm.Trellises);
        Assert.Equal("storefront", item.Name);
        Assert.True(item.NeedsApproval);
        Assert.Equal(item, vm.Selected);
        var pending = Assert.Single(vm.PendingTrust);
        Assert.Equal(file.Replace('\\', '/'), pending.Path);
        Assert.False(pending.Changed);
        Assert.True(vm.PendingIsTrellis);
        Assert.Null(vm.Plan);
    }

    [Fact]
    public async Task A_guessed_name_steps_aside_for_a_trellis_already_registered_under_it()
    {
        using var store = new TempStore();
        Directory.CreateDirectory(Path.Combine(store.Root, "a"));
        Directory.CreateDirectory(Path.Combine(store.Root, "b"));
        var first = Path.Combine(store.Root, "a", "storefront.trellis.cs");
        var second = Path.Combine(store.Root, "b", "storefront.trellis.cs");
        File.WriteAllText(first, "using Sprig;\nreturn Trellis.Describe(\"one\").Emit(args);");
        File.WriteAllText(second, "using Sprig;\nreturn Trellis.Describe(\"two\").Emit(args);");
        var vm = new TrellisesViewModel(new AppServices(store.Root));

        vm.NewPath = first;
        await vm.AddCommand.ExecuteAsync(null);
        vm.NewPath = second;
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(["storefront", "storefront-2"], vm.Trellises.Select(t => t.Name));
    }

    [Fact]
    public async Task A_path_that_is_not_a_file_is_refused_with_a_reason_and_the_modal_stays_open()
    {
        using var store = new TempStore();
        var vm = new TrellisesViewModel(new AppServices(store.Root));
        vm.OpenAddCommand.Execute(null);
        vm.NewPath = Path.Combine(store.Root, "nope.trellis.cs");

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.IsAddOpen);
        Assert.True(vm.AddFailed);
        Assert.Contains("Choose a trellis file", vm.AddMessage);
        Assert.Empty(vm.Trellises);
    }

    [Theory]
    [InlineData("C:/x/storefront.trellis.cs", "storefront")]
    [InlineData("C:/x/Shop.Trellis.CS", "Shop")]
    [InlineData("C:/x/shop.cs", "shop")]
    [InlineData("C:/x/.trellis.cs", "trellis")]
    [InlineData("C:/x/my shop!.trellis.cs", "my-shop")]
    public void The_id_a_trellis_is_offered_comes_from_its_file(string path, string expected)
        => Assert.Equal(expected, Sprig.Engine.Trellises.TrellisRegistry.IdFromFile(path));

    [Fact]
    public void The_plan_view_lists_wiring_between_repos_and_masks_passwords()
    {
        var at = new SourceLocation(@"C:\tb\shop-web\sprig.cs", 7);
        var plan = new Plan("storefront", "plan", "stable", "main",
            [new PlannedPort("shop-services", "catalog-api", "http", 8000, null, null)],
            [
                new PlannedApp("shop-web", "shop-web", "web", "node", ".",
                    [new PlannedEnv("VITE_CATALOG_URL", new PlannedValue("{import.catalog-url}", "http://localhost:8000",
                        ["import catalog-url ← shop-services", "catalog-api.http port 8000"],
                        [new ValueSource("shop-services", "catalog-api", "http", "catalog-url")]), ".env.local", at)],
                    null, [], []),
                new PlannedApp("shop-services", "shop-services", "catalog-api", "dotnet", "src/Catalog.Api",
                    [new PlannedEnv("Db", new PlannedValue("x", "Host=a;Password=hunter2", []), null, at)], null, [], []),
            ],
            [], [], [], [new PlanProblem(ProblemSeverity.Warning, "careful", at)]);

        var view = new PlanView(plan, @"C:\tb");

        var wire = Assert.Single(view.Wiring);
        Assert.Equal("shop-web / web", wire.Consumer);
        Assert.Equal("shop-services / catalog-api (catalog-url)", wire.Provider);
        Assert.Equal("http://localhost:8000", wire.Value);
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
