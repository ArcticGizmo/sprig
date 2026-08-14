using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

public class RepoInputEditorTests
{
    static RepoInputEditorViewModel Editor(
        IReadOnlyList<(string Input, string Expr)> rows,
        IReadOnlyList<string> ports,
        Action<string>? createPort = null)
    {
        var group = new RepoBindingGroup("web");
        foreach (var (input, expr) in rows)
            group.Rows.Add(new BindingRow(input, null) { Expression = expr });
        return new RepoInputEditorViewModel(group, ports, createPort ?? (_ => { }));
    }

    [Fact]
    public void Classifies_each_expression_shape_into_the_right_kind()
    {
        var e = Editor(
            [("a", "${sprig.ports.api}"), ("b", "${sprig.workspace}"), ("c", "5432"),
             ("d", "http://localhost:${sprig.ports.api}")],
            ["api"]);

        Assert.Equal(InputSourceKind.Port, e.Rows[0].Kind);
        Assert.Equal("api", e.Rows[0].PortName);
        Assert.Equal(InputSourceKind.Workspace, e.Rows[1].Kind);
        Assert.Equal(InputSourceKind.Literal, e.Rows[2].Kind);
        Assert.Equal("5432", e.Rows[2].LiteralValue);
        Assert.Equal(InputSourceKind.Custom, e.Rows[3].Kind);
        Assert.Equal("http://localhost:${sprig.ports.api}", e.Rows[3].CustomExpression);
    }

    [Fact]
    public void Choosing_a_port_composes_the_port_token_back_onto_the_binding()
    {
        var group = new RepoBindingGroup("web");
        var row = new BindingRow("apiUrl", null) { Expression = "" };
        group.Rows.Add(row);
        var e = new RepoInputEditorViewModel(group, ["api", "db"], _ => { });

        e.Rows[0].Kind = InputSourceKind.Port;
        e.Rows[0].PortName = "db";

        Assert.Equal("${sprig.ports.db}", row.Expression);
    }

    [Fact]
    public void Switching_to_workspace_or_literal_writes_the_expected_expression()
    {
        var group = new RepoBindingGroup("web");
        var row = new BindingRow("x", null) { Expression = "${sprig.ports.api}" };
        group.Rows.Add(row);
        var e = new RepoInputEditorViewModel(group, ["api"], _ => { });

        e.Rows[0].Kind = InputSourceKind.Workspace;
        Assert.Equal("${sprig.workspace}", row.Expression);

        e.Rows[0].Kind = InputSourceKind.Literal;
        e.Rows[0].LiteralValue = "9000";
        Assert.Equal("9000", row.Expression);
    }

    [Fact]
    public void Switching_to_port_defaults_to_the_first_available_so_the_binding_isnt_blanked()
    {
        var group = new RepoBindingGroup("web");
        var row = new BindingRow("x", null) { Expression = "5432" };
        group.Rows.Add(row);
        var e = new RepoInputEditorViewModel(group, ["api", "db"], _ => { });

        e.Rows[0].Kind = InputSourceKind.Port;

        Assert.Equal("api", e.Rows[0].PortName);
        Assert.Equal("${sprig.ports.api}", row.Expression);
    }

    [Fact]
    public void Editing_the_binding_externally_reclassifies_the_row()
    {
        var group = new RepoBindingGroup("web");
        var row = new BindingRow("x", null) { Expression = "5432" };
        group.Rows.Add(row);
        var e = new RepoInputEditorViewModel(group, ["api"], _ => { });
        Assert.Equal(InputSourceKind.Literal, e.Rows[0].Kind);

        row.Expression = "${sprig.ports.api}"; // e.g. the patchbay rewired it

        Assert.Equal(InputSourceKind.Port, e.Rows[0].Kind);
        Assert.Equal("api", e.Rows[0].PortName);
    }

    [Fact]
    public void Add_port_declares_it_and_offers_it_locally()
    {
        string? created = null;
        var e = Editor([("x", "")], ["api"], name => created = name);

        e.NewPortName = "cache";
        e.AddPortCommand.Execute(null);

        Assert.Equal("cache", created);
        Assert.Contains("cache", e.AvailablePorts);
        Assert.Equal("", e.NewPortName); // cleared after adding
    }

    [Fact]
    public void Detach_stops_the_row_tracking_its_binding()
    {
        var group = new RepoBindingGroup("web");
        var row = new BindingRow("x", null) { Expression = "5432" };
        group.Rows.Add(row);
        var e = new RepoInputEditorViewModel(group, ["api"], _ => { });

        e.Detach();
        row.Expression = "${sprig.workspace}"; // change after detach

        Assert.Equal(InputSourceKind.Literal, e.Rows[0].Kind); // stale on purpose — no longer tracking
    }
}
