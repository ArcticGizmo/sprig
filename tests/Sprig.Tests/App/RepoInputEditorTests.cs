using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

public class RepoInputEditorTests
{
    static (RepoInputEditorViewModel editor, RepoBindingGroup group) Build(
        IReadOnlyList<(string Input, string Expr)> rows,
        IReadOnlyList<string> ports)
    {
        var group = new RepoBindingGroup("web");
        foreach (var (input, expr) in rows)
            group.Rows.Add(new BindingRow(input, null) { Expression = expr });
        return (new RepoInputEditorViewModel(group, ports), group);
    }

    [Fact]
    public void Rows_expose_the_binding_expression_for_direct_editing()
    {
        var (e, _) = Build([("a", "${sprig.ports.api}"), ("b", "5432")], ["api"]);

        Assert.Equal("${sprig.ports.api}", e.Rows[0].Expression);
        Assert.Equal("5432", e.Rows[1].Expression);
    }

    [Fact]
    public void Editing_a_rows_expression_writes_straight_to_the_binding()
    {
        var (e, group) = Build([("apiUrl", "")], ["api"]);

        e.Rows[0].Expression = "http://localhost:${sprig.ports.api}";

        Assert.Equal("http://localhost:${sprig.ports.api}", group.Rows[0].Expression);
    }

    [Fact]
    public void An_external_edit_is_reflected_back_into_the_row()
    {
        var (e, group) = Build([("x", "5432")], ["api"]);
        var raised = false;
        e.Rows[0].PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(RepoInputRowViewModel.Expression)) raised = true; };

        group.Rows[0].Expression = "${sprig.ports.api}"; // e.g. the patchbay rewired it

        Assert.True(raised);
        Assert.Equal("${sprig.ports.api}", e.Rows[0].Expression);
    }

    [Fact]
    public void Variables_offer_the_workspace_and_each_declared_port()
    {
        var (e, _) = Build([("x", "")], ["api", "db"]);

        Assert.Equal(new[] { "workspace", "ports.api", "ports.db" }, e.Variables);
    }

    [Fact]
    public void Detach_stops_the_row_tracking_its_binding()
    {
        var (e, group) = Build([("x", "5432")], ["api"]);

        e.Detach();
        var raised = false;
        e.Rows[0].PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(RepoInputRowViewModel.Expression)) raised = true; };
        group.Rows[0].Expression = "${sprig.workspace}"; // change after detach

        Assert.False(raised); // no longer tracking
    }
}
