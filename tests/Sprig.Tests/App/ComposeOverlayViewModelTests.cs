using System.Linq;
using Sprig.App.ViewModels;
using Sprig.Core.Config;

namespace Sprig.Tests.App;

public class ComposeOverlayViewModelTests
{
    private const string Sample =
        "services:\n" +
        "  db:\n" +
        "    image: postgres:16\n" +
        "    container_name: myapp-db\n" +
        "    ports:\n" +
        "      - \"5432:5432\"\n";

    private static ComposeRunViewModel Token(ComposeOverlayViewModel vm, params string[] path)
        => vm.Lines.SelectMany(l => l.Runs).Single(r => r.IsToken && r.Path.SequenceEqual(path));

    [Fact]
    public void Applying_a_template_records_the_override_and_updates_the_rendered_value()
    {
        var vm = new ComposeOverlayViewModel(Sample);

        var cn = Token(vm, "services", "db", "container_name");
        Assert.False(cn.IsApplied);

        cn.Draft = "myapp-db--${sprig.workspace}";
        vm.ApplyCommand.Execute(cn);

        var overrides = vm.ToOverrides();
        Assert.Contains(overrides, o =>
            o.Path.SequenceEqual(new[] { "services", "db", "container_name" }) &&
            o.Template == "myapp-db--${sprig.workspace}");

        // Rebuild replaced the runs — the token now renders the template and reads as applied.
        var after = Token(vm, "services", "db", "container_name");
        Assert.True(after.IsApplied);
        Assert.Equal("myapp-db--${sprig.workspace}", after.Display);
        Assert.True(vm.HasOverrides);
    }

    [Fact]
    public void Token_editor_opens_on_the_current_value()
    {
        var vm = new ComposeOverlayViewModel(Sample);
        var port = Token(vm, "services", "db", "ports", "0");
        // No guessed ${sprig.ports.*} pre-fill — a repo references its own inputs, so the editor opens
        // on the current value and the user templates it (with autocomplete).
        Assert.Equal("\"5432:5432\"", port.Draft);
    }

    [Fact]
    public void Exposes_the_supplied_variable_names()
    {
        var vm = new ComposeOverlayViewModel(Sample, variables: new[] { "workspace", "dbPort" });
        Assert.Equal(new[] { "workspace", "dbPort" }, vm.Variables);
    }

    [Fact]
    public void Seeds_from_existing_overrides_and_can_remove_them()
    {
        var seed = new[]
        {
            new ComposeOverride { Path = ["services", "db", "container_name"], Template = "x--${sprig.workspace}" },
        };
        var vm = new ComposeOverlayViewModel(Sample, seed);

        var cn = Token(vm, "services", "db", "container_name");
        Assert.True(cn.IsApplied);
        Assert.Equal("x--${sprig.workspace}", cn.Display);

        vm.RemoveCommand.Execute(cn);

        Assert.Empty(vm.ToOverrides());
        Assert.False(Token(vm, "services", "db", "container_name").IsApplied);
        Assert.False(vm.HasOverrides);
    }

    [Fact]
    public void Applying_the_original_value_verbatim_stores_nothing()
    {
        var vm = new ComposeOverlayViewModel(Sample);
        var image = Token(vm, "services", "db", "image");

        image.Draft = "postgres:16"; // unchanged from the original
        vm.ApplyCommand.Execute(image);

        Assert.Empty(vm.ToOverrides());
    }

    [Fact]
    public void Override_referencing_an_undeclared_input_is_flagged()
    {
        var vm = new ComposeOverlayViewModel(Sample, variables: new[] { "workspace" });

        var cn = Token(vm, "services", "db", "container_name");
        cn.Draft = "${sprig.workspace}-db";   // workspace is known
        vm.ApplyCommand.Execute(cn);
        Assert.False(Token(vm, "services", "db", "container_name").ReferencesUnknownInput);

        var port = Token(vm, "services", "db", "ports", "0");
        port.Draft = "${sprig.dbPort}:5432";  // dbPort isn't declared
        vm.ApplyCommand.Execute(port);
        Assert.True(Token(vm, "services", "db", "ports", "0").ReferencesUnknownInput);

        // the inspector agrees: one flagged, one not
        Assert.Contains(vm.Overrides, o => o.ReferencesUnknownInput);
        Assert.Contains(vm.Overrides, o => !o.ReferencesUnknownInput);
    }

    [Fact]
    public void Unparseable_file_surfaces_an_error_and_offers_no_tokens()
    {
        var vm = new ComposeOverlayViewModel("services:\n  db:\n   - bad: : :\n\tmixed\n");

        Assert.True(vm.HasError);
        Assert.NotNull(vm.Error);
        Assert.NotEmpty(vm.Lines);
        Assert.DoesNotContain(vm.Lines.SelectMany(l => l.Runs), r => r.IsToken);
    }
}
