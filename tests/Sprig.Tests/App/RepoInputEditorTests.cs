using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

public class RepoInputEditorTests
{
    static (RepoInputEditorViewModel editor, RepoBindingGroup group) Build(
        IReadOnlyList<(string Input, string Expr)> rows,
        IReadOnlyList<string> ports,
        Action<string>? createPort = null)
    {
        var group = new RepoBindingGroup("web");
        foreach (var (input, expr) in rows)
            group.Rows.Add(new BindingRow(input, null) { Expression = expr });
        var editor = new RepoInputEditorViewModel(group, ports, createPort ?? (_ => { }));
        return (editor, group);
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
    public void A_rows_new_port_is_declared_and_referenced_in_place()
    {
        string? created = null;
        var (e, group) = Build([("apiUrl", "")], [], name => created = name); // no ports defined yet

        e.Rows[0].NewPortName = "api_port";
        e.Rows[0].ConfirmAddPortCommand.Execute(null);

        Assert.Equal("api_port", created);
        Assert.Equal("${sprig.ports.api_port}", e.Rows[0].Expression);
        Assert.Equal("${sprig.ports.api_port}", group.Rows[0].Expression);
    }

    [Fact]
    public void The_footer_add_port_declares_without_binding_anything()
    {
        string? created = null;
        var (e, group) = Build([("x", "")], ["api"], name => created = name);

        e.NewPortName = "cache";
        e.AddPortCommand.Execute(null);

        Assert.Equal("cache", created);
        Assert.Contains("ports.cache", e.Variables);
        Assert.Equal("", e.NewPortName);          // cleared
        Assert.Equal("", group.Rows[0].Expression); // nothing bound
    }

    [Fact]
    public void Cancelling_the_inline_new_port_leaves_the_binding_untouched()
    {
        var (e, group) = Build([("x", "5432")], ["api"]);

        e.Rows[0].StartAddPortCommand.Execute(null);
        Assert.True(e.Rows[0].AddingPort);
        e.Rows[0].NewPortName = "oops";
        e.Rows[0].CancelAddPortCommand.Execute(null);

        Assert.False(e.Rows[0].AddingPort);
        Assert.Equal("5432", group.Rows[0].Expression); // unchanged
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
