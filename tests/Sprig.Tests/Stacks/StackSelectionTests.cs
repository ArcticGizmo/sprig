using System.Collections.Generic;
using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

/// <summary>
/// A partial workspace keeps a subset of a stack's repos. These pin the two rules that follow from
/// that: the selection is validated and returned in stack order, and a stack port left with no
/// remaining consumer is orphaned (so create must not provision it).
/// </summary>
public class StackSelectionTests
{
    // api owns api_port; web consumes api_port too (its URL) and owns web_port; admin_port belongs to
    // no repo's binding at all.
    static StackDefinition Stack() => new()
    {
        Name = "web+api",
        Repos = ["api", "web"],
        Ports = ["api_port", "web_port", "admin_port"],
        Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
            ["web"] = new Dictionary<string, string>
            {
                ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",
                ["devPort"] = "${sprig.ports.web_port}",
            },
        },
    };

    [Fact]
    public void No_selection_means_the_whole_stack()
    {
        Assert.Equal(["api", "web"], StackSelection.Include(Stack(), null));
        Assert.Empty(StackSelection.Exclude(Stack(), null));
        Assert.Empty(StackSelection.OrphanedPorts(Stack(), Stack().Repos));
    }

    [Fact]
    public void Selection_is_returned_in_stack_order()
        => Assert.Equal(["api", "web"], StackSelection.Include(Stack(), ["web", "api"]));

    [Fact]
    public void An_empty_selection_is_a_mistake_not_a_shorthand_for_all()
        => Assert.Throws<StackException>(() => StackSelection.Include(Stack(), []));

    [Fact]
    public void An_unknown_repo_is_rejected()
    {
        var ex = Assert.Throws<StackException>(() => StackSelection.Include(Stack(), ["api", "mobile"]));
        Assert.Contains("'mobile'", ex.Message);
    }

    [Fact]
    public void Dropping_a_repo_orphans_only_the_ports_nothing_else_references()
    {
        // web is the only consumer of web_port, so it's orphaned; api_port survives (api still uses it),
        // and admin_port is nobody's, so the deselection doesn't touch it.
        Assert.Equal(["web_port"], StackSelection.OrphanedPorts(Stack(), ["api"]));
        Assert.Equal(["api_port", "admin_port"], StackSelection.ProvisionedPorts(Stack(), ["api"]));
    }

    [Fact]
    public void A_port_a_kept_repo_still_references_is_not_orphaned()
    {
        // Dropping api leaves web pointing at api_port, so the port stays provisioned even though
        // nothing serves it — web's env would otherwise resolve to a hole.
        Assert.Empty(StackSelection.OrphanedPorts(Stack(), ["web"]));
        Assert.Equal(["api"], StackSelection.Exclude(Stack(), ["web"]));
    }
}
