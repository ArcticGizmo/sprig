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
}
