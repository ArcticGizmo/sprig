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
    // --- Auto-wire on selection, made safe by port provenance ---------------

    [Fact]
    public void Selecting_repos_while_creating_wires_them_by_convention()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        // No Auto-wire click: the canvas arrives wired, so the first task is reviewing a guess rather than
        // authoring wiring before you know what wiring is. (An input already named "port" keeps that name —
        // StackAutowire only appends the _port suffix when it isn't already there.)
        Assert.NotEmpty(vm.Ports);
        Assert.Equal("${sprig.ports.port}", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void Two_repos_declaring_the_same_input_never_end_up_sharing_one_port()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "worker", ("port", "6000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "svc" };

        // Selected one at a time, which is what a user does — and what previously broke this. Auto-wire
        // reuses a port whose name matches, so without provenance the second repo adopted the port invented
        // for the first, pointing two services' own listening ports at one number: a runtime collision, and
        // the reason the first attempt at this was reverted (docs/guided-tour-plan.md §11.3).
        vm.RepoChoices.First(c => c.Name == "api").IsSelected = true;
        vm.RepoChoices.First(c => c.Name == "worker").IsSelected = true;

        Assert.Equal(2, vm.Ports.Count);
        Assert.NotEqual(Row(vm, "api", "port").Expression, Row(vm, "worker", "port").Expression);
    }

    [Fact]
    public void Auto_wiring_never_overwrites_a_binding_the_user_typed()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "web", ("apiUrl", "http://x")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
        vm.RepoChoices.First(c => c.Name == "api").IsSelected = true;
        Row(vm, "api", "port").Expression = "${sprig.ports.mine}";

        // Adding a second repo re-proposes; the hand-written expression must survive every recompute.
        vm.RepoChoices.First(c => c.Name == "web").IsSelected = true;
        vm.AutoWireCommand.Execute(null);

        Assert.Equal("${sprig.ports.mine}", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void A_port_the_user_named_survives_every_recompute()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "web", ("apiUrl", "http://x")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
        vm.RepoChoices.First(c => c.Name == "api").IsSelected = true;

        vm.AddNamedPortCommand.Execute("shared_port");
        Assert.Contains(vm.Ports, p => p.Name == "shared_port");

        // Selecting another repo re-proposes, which deletes auto-wire's own ports. A port the user added by
        // hand is not auto-wire's to delete.
        vm.RepoChoices.First(c => c.Name == "web").IsSelected = true;

        Assert.Contains(vm.Ports, p => p.Name == "shared_port");
    }

    [Fact]
    public void Renaming_an_auto_port_makes_it_the_users_and_it_stops_being_recomputed()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "web", ("apiUrl", "http://x")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
        vm.RepoChoices.First(c => c.Name == "api").IsSelected = true;

        // Renaming is an act of intent, so the port (and the binding that followed the rename) is now the
        // user's — a later recompute must not discard either.
        vm.Ports.Single().Name = "api_listen";
        Assert.Equal("${sprig.ports.api_listen}", Row(vm, "api", "port").Expression);

        vm.RepoChoices.First(c => c.Name == "web").IsSelected = true;

        Assert.Contains(vm.Ports, p => p.Name == "api_listen");
        Assert.Equal("${sprig.ports.api_listen}", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void Auto_wire_is_idempotent()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "worker", ("port", "6000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "svc" };
        foreach (var c in vm.RepoChoices) c.IsSelected = true;

        var ports = vm.Ports.Select(p => p.Name).OrderBy(n => n).ToList();
        vm.AutoWireCommand.Execute(null);
        vm.AutoWireCommand.Execute(null);

        // Discarding-then-re-proposing must converge, not accumulate _2/_3 suffixes on every pass.
        Assert.Equal(ports, vm.Ports.Select(p => p.Name).OrderBy(n => n).ToList());
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
