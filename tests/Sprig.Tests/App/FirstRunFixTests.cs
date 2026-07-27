using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Stacks;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the first-run comprehension fixes from docs/guided-tour-plan.md §11.2 — the changes that make
/// the wall of inputs smaller, so the coachmark script doesn't have to apologise for it.
/// </summary>
public class FirstRunFixTests
{
    // --- Auto-wire stays an explicit action ---------------------------------

    [Fact]
    public void Selecting_repos_does_not_wire_anything_on_its_own()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "worker", ("port", "6000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "svc" };
        foreach (var c in vm.RepoChoices) c.IsSelected = true;

        // Guards a hazard that a pre-wired canvas would reintroduce (docs/guided-tour-plan.md §11.4):
        // StackAutowire reuses a port whose name matches, so wiring as each repo is selected makes the
        // second repo adopt the first repo's port. Two services that each declare `port` would then be
        // pointed at ONE port and collide at runtime. Batch auto-wire hands out distinct ports instead.
        Assert.Empty(vm.Ports);
        Assert.Equal("", Row(vm, "api", "port").Expression);
        Assert.Equal("", Row(vm, "worker", "port").Expression);

        vm.AutoWireCommand.Execute(null);

        // Explicit and batched: two ports, not one shared between them.
        Assert.Equal(2, vm.Ports.Count);
        Assert.NotEqual(Row(vm, "api", "port").Expression, Row(vm, "worker", "port").Expression);
    }

    [Fact]
    public void Auto_wiring_never_overwrites_a_binding_the_user_typed()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
        vm.RepoChoices.First(c => c.Name == "api").IsSelected = true;
        Row(vm, "api", "port").Expression = "${sprig.ports.mine}";

        vm.AutoWireCommand.Execute(null);

        Assert.Equal("${sprig.ports.mine}", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void Editing_an_existing_stack_shows_exactly_what_was_saved()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        // A saved stack whose single input is deliberately bound to a literal, with no ports at all.
        services.Stacks.Save(new StackDefinition
        {
            Name = "literal",
            Repos = ["api"],
            Ports = [],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["api"] = new Dictionary<string, string> { ["port"] = "9999" },
            },
        });

        var vm = new StacksViewModel(services, new Navigator());
        vm.Selected = vm.Stacks.Single(x => x.Name == "literal");
        vm.EditSelectedCommand.Execute(null);

        // EditSelected selects the repos before applying the stored bindings, so anything that wired on
        // selection would invent a port here and keep it. Editing must be faithful: what was saved is
        // what you see, literals included.
        Assert.Equal("9999", Row(vm, "api", "port").Expression);
        Assert.Empty(vm.Ports);
    }

    // --- Scaffold notes -----------------------------------------------------

    [Fact]
    public async Task Adding_a_repo_without_a_config_explains_what_sprig_filled_in()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("needs-init");
        var services = new AppServices(s.Root);

        // An env file with a port-shaped key is exactly what InitInspector turns into a declared input.
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "PORT=4000\nNAME=x\n");
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), ".env\n");

        var vm = new ReposViewModel(services) { NewPath = repo.Path };
        await vm.ConfirmAddCommand.ExecuteAsync(null);

        // The notes are the CLI's long-standing explanation of the scaffold; the app used to discard them,
        // leaving a newcomer with a pre-filled form and no account of where any of it came from.
        Assert.True(vm.HasScaffoldNotes, "expected the scaffold explanation to be surfaced");
        Assert.NotEmpty(vm.ScaffoldNotes);

        vm.DismissScaffoldNotesCommand.Execute(null);
        Assert.False(vm.HasScaffoldNotes);
    }

    [Fact]
    public async Task Adding_a_repo_that_already_has_a_config_explains_nothing()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("has-config");
        var services = new AppServices(s.Root);

        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"),
            """{ "schema": 2, "name": "has-config" }""");

        var vm = new ReposViewModel(services) { NewPath = repo.Path };
        await vm.ConfirmAddCommand.ExecuteAsync(null);

        // Nothing was scaffolded, so there is nothing to account for.
        Assert.False(vm.HasScaffoldNotes);
    }

    // --- Progressive disclosure of port restrictions ------------------------

    [Fact]
    public void Port_restriction_stays_behind_a_link_until_it_is_wanted()
    {
        var row = new InputEditRow(_ => { });

        // Almost every input leaves this blank, so it must not sit at the same weight as the name.
        Assert.True(row.ShowRestrictLink);
        Assert.False(row.ShowRestrictBox);

        row.RestrictCommand.Execute(null);

        Assert.False(row.ShowRestrictLink);
        Assert.True(row.ShowRestrictBox);
    }

    [Fact]
    public void An_existing_port_restriction_shows_itself_without_being_asked()
    {
        // A repo that already restricts ports must never hide that behind a link the user has to find.
        var row = new InputEditRow(_ => { }) { AllowedPorts = "8100-8103" };

        Assert.False(row.ShowRestrictLink);
        Assert.True(row.ShowRestrictBox);
    }

    static BindingRow Row(StacksViewModel vm, string repo, string input) =>
        vm.Bindings.First(g => g.Repo == repo).Rows.First(r => r.Input == input);
}
