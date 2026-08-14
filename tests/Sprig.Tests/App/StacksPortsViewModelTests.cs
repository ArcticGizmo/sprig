using Sprig.App;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// The Stacks builder's "Defined ports" panel: ports are declared here (not in the per-repo editor), and
/// a binding that references a port the stack doesn't define is surfaced as "referenced but not defined"
/// so it can be accepted into existence — the flow that keeps the red pins on the graph fixable.
/// </summary>
public class StacksPortsViewModelTests
{
    static StacksViewModel NewBuilderWithWeb(out AppServices services, TempStore s)
    {
        services = new AppServices(s.Root);
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "web", ("apiUrl", "http://localhost:4000")));
        var vm = new StacksViewModel(services, new Navigator());
        vm.NewStackCommand.Execute(null);
        vm.RepoChoices.Single(c => c.Name == "web").IsSelected = true;
        return vm;
    }

    [Fact]
    public void A_binding_referencing_an_undeclared_port_is_surfaced_then_accepted()
    {
        using var s = new TempStore();
        var vm = NewBuilderWithWeb(out _, s);

        // Point the input at a port nobody has defined — it should show up as undeclared.
        var row = vm.Bindings.Single().Rows.Single(r => r.Input == "apiUrl");
        row.Expression = "${sprig.ports.ghost}";

        Assert.True(vm.HasUndeclaredPorts);
        Assert.Contains("ghost", vm.UndeclaredPorts);
        Assert.DoesNotContain("ghost", vm.PortNames);

        // Accepting it declares the port; it drops out of "undeclared" and joins the defined set.
        vm.AcceptPortCommand.Execute("ghost");

        Assert.DoesNotContain("ghost", vm.UndeclaredPorts);
        Assert.False(vm.HasUndeclaredPorts);
        Assert.Contains("ghost", vm.PortNames);
    }

    [Fact]
    public void The_defined_ports_add_field_declares_a_port_and_clears()
    {
        using var s = new TempStore();
        var vm = NewBuilderWithWeb(out _, s);

        vm.DefinedPortEntry = "cache_port";
        vm.AddDefinedPortCommand.Execute(null);

        Assert.Contains("cache_port", vm.PortNames);
        Assert.Equal("", vm.DefinedPortEntry);
    }

    [Fact]
    public void A_port_nothing_references_is_flagged_unused_until_something_uses_it()
    {
        using var s = new TempStore();
        var vm = NewBuilderWithWeb(out _, s);

        vm.DefinedPortEntry = "spare_port";
        vm.AddDefinedPortCommand.Execute(null);
        Assert.False(vm.Ports.Single(p => p.Name == "spare_port").InUse);

        vm.Bindings.Single().Rows.Single(r => r.Input == "apiUrl").Expression = "${sprig.ports.spare_port}";
        Assert.True(vm.Ports.Single(p => p.Name == "spare_port").InUse);
    }

    [Fact]
    public void Committing_an_inline_rename_renames_the_port_and_rewrites_its_bindings()
    {
        using var s = new TempStore();
        var vm = NewBuilderWithWeb(out _, s);

        var row = vm.Bindings.Single().Rows.Single(r => r.Input == "apiUrl");
        row.Expression = "${sprig.ports.api_port}";
        vm.AcceptPortCommand.Execute("api_port");
        var port = vm.Ports.Single(p => p.Name == "api_port");

        port.StartEditCommand.Execute(null);
        port.EditName = "backend_port";
        vm.CommitPortRenameCommand.Execute(port);

        Assert.Equal("backend_port", port.Name);
        Assert.False(port.Editing);
        Assert.Equal("${sprig.ports.backend_port}", row.Expression); // propagated to the binding
    }

    [Fact]
    public void Moving_a_port_reorders_it_and_reindexes_the_previewed_numbers()
    {
        using var s = new TempStore();
        var vm = NewBuilderWithWeb(out var services, s);
        var start = services.Settings.Get().PortRangeStart;

        vm.DefinedPortEntry = "first_port";
        vm.AddDefinedPortCommand.Execute(null);
        vm.DefinedPortEntry = "second_port";
        vm.AddDefinedPortCommand.Execute(null);

        var named = vm.Ports.Where(p => p.Name.Trim().Length > 0).ToList();
        var second = named.Single(p => p.Name == "second_port");
        var before = vm.Ports.IndexOf(second);

        vm.MovePortUpCommand.Execute(second);

        Assert.True(vm.Ports.IndexOf(second) < before);              // it moved up
        // Previews follow position from the configured range start.
        var reordered = vm.Ports.Where(p => p.Name.Trim().Length > 0).ToList();
        for (var i = 0; i < reordered.Count; i++)
            Assert.Equal((start + i).ToString(), reordered[i].Preview);
    }

    [Fact]
    public void A_rename_that_would_collide_with_another_port_is_refused()
    {
        using var s = new TempStore();
        var vm = NewBuilderWithWeb(out _, s);

        vm.DefinedPortEntry = "a_port";
        vm.AddDefinedPortCommand.Execute(null);
        vm.DefinedPortEntry = "b_port";
        vm.AddDefinedPortCommand.Execute(null);

        var a = vm.Ports.Single(p => p.Name == "a_port");
        a.StartEditCommand.Execute(null);
        a.EditName = "b_port"; // collides
        vm.CommitPortRenameCommand.Execute(a);

        Assert.Equal("a_port", a.Name); // unchanged
    }
}
