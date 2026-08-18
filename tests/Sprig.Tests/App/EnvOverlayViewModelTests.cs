using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Sprig.App.ViewModels;
using Sprig.Core.Env;

namespace Sprig.Tests.App;

public class EnvOverlayViewModelTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> NoExamples =
        new Dictionary<string, IReadOnlyList<EnvExample>>();

    private static EnvKeyViewModel Row(EnvOverlayViewModel vm, string key)
        => vm.Keys.Single(r => r.Key == key);

    [Fact]
    public void Applying_a_template_records_the_override_and_renders_it_applied()
    {
        var vm = new EnvOverlayViewModel(["PORT", "API_KEY"], NoExamples, variables: ["workspace", "dbPort"]);

        var port = Row(vm, "PORT");
        Assert.False(port.IsApplied);
        Assert.Equal("override", port.Display);   // dimmed placeholder until set

        port.Draft = "${sprig.dbPort}";
        vm.ApplyCommand.Execute(port);

        Assert.Equal("${sprig.dbPort}", vm.ToSet()["PORT"]);

        var after = Row(vm, "PORT");
        Assert.True(after.IsApplied);
        Assert.Equal("${sprig.dbPort}", after.Display);
        Assert.True(vm.HasOverrides);
    }

    [Fact]
    public void Seeds_from_existing_set_and_can_remove()
    {
        var seed = new Dictionary<string, string> { ["PORT"] = "${sprig.dbPort}" };
        var vm = new EnvOverlayViewModel(["PORT"], NoExamples, seed);

        var port = Row(vm, "PORT");
        Assert.True(port.IsApplied);
        Assert.Equal("${sprig.dbPort}", port.Display);

        vm.RemoveCommand.Execute(port);

        Assert.Empty(vm.ToSet());
        Assert.False(Row(vm, "PORT").IsApplied);
        Assert.False(vm.HasOverrides);
    }

    [Fact]
    public void Applying_a_blank_value_clears_the_override()
    {
        var vm = new EnvOverlayViewModel(["PORT"], NoExamples, new Dictionary<string, string> { ["PORT"] = "x" });

        var port = Row(vm, "PORT");
        port.Draft = "   ";
        vm.ApplyCommand.Execute(port);

        Assert.Empty(vm.ToSet());
    }

    [Fact]
    public void Surfaces_example_values_and_uses_the_first_as_the_watermark()
    {
        var examples = new Dictionary<string, IReadOnlyList<EnvExample>>
        {
            ["PORT"] = [new EnvExample(".env.example", "8080")],
        };
        var vm = new EnvOverlayViewModel(["PORT", "API_KEY"], examples);

        var port = Row(vm, "PORT");
        Assert.True(port.HasExamples);
        Assert.Equal("8080", port.ValueWatermark);

        var api = Row(vm, "API_KEY");
        Assert.False(api.HasExamples);
        Assert.Equal("${sprig.input}", api.ValueWatermark);   // falls back to the generic hint
    }

    [Fact]
    public void Example_values_shared_across_files_are_combined_under_one_header()
    {
        var examples = new Dictionary<string, IReadOnlyList<EnvExample>>
        {
            ["PORT"] =
            [
                new EnvExample(".env.local", "8080"),
                new EnvExample(".env.example", "8080"),   // same value, different file
                new EnvExample(".env.dist", "9090"),      // a different value
            ],
        };
        var vm = new EnvOverlayViewModel(["PORT"], examples);

        var port = Row(vm, "PORT");
        Assert.Equal(2, port.Examples.Count);                       // two distinct values, not three rows
        Assert.Equal(".env.local, .env.example",
            port.Examples.Single(e => e.Value == "8080").Sources);  // sharing files combined, first-seen order
        Assert.Equal(".env.dist", port.Examples.Single(e => e.Value == "9090").Sources);
    }

    [Fact]
    public void Add_key_introduces_a_row_for_a_key_no_template_declares()
    {
        var vm = new EnvOverlayViewModel([], NoExamples);
        Assert.False(vm.HasKeys);

        vm.NewKey = "CUSTOM_TOKEN";
        vm.AddKeyCommand.Execute(null);

        var row = Row(vm, "CUSTOM_TOKEN");
        row.Draft = "literal-value";
        vm.ApplyCommand.Execute(row);

        Assert.Equal("literal-value", vm.ToSet()["CUSTOM_TOKEN"]);
    }

    [Fact]
    public void A_seeded_override_for_an_undeclared_key_still_shows_as_a_row()
    {
        var seed = new Dictionary<string, string> { ["LEGACY_KEY"] = "v" };
        var vm = new EnvOverlayViewModel(["PORT"], NoExamples, seed);

        // LEGACY_KEY isn't in the file/template keys, but the saved override keeps it visible/editable.
        var legacy = Row(vm, "LEGACY_KEY");
        Assert.True(legacy.IsApplied);
        Assert.Equal("v", vm.ToSet()["LEGACY_KEY"]);
    }

    [Fact]
    public void Exposes_the_supplied_variable_names()
    {
        var vm = new EnvOverlayViewModel(["PORT"], NoExamples, variables: ["workspace", "dbPort"]);
        Assert.Equal(new[] { "workspace", "dbPort" }, vm.Variables);
    }

    [Fact]
    public void Override_referencing_an_undeclared_input_is_flagged_and_clears_when_declared()
    {
        var vars = new ObservableCollection<string> { "workspace", "port" };
        var vm = new EnvOverlayViewModel(["PORT", "HOST"], NoExamples, variables: vars);

        // references an input that isn't declared → flagged (renders red) in row and inspector
        var host = Row(vm, "HOST");
        host.Draft = "${sprig.apiHost}";
        vm.ApplyCommand.Execute(host);
        Assert.True(Row(vm, "HOST").ReferencesUnknownInput);
        Assert.True(vm.Overrides.Single(o => o.Key == "HOST").ReferencesUnknownInput);

        // a declared input (and a plain literal) are fine
        var port = Row(vm, "PORT");
        port.Draft = "${sprig.port}";
        vm.ApplyCommand.Execute(port);
        Assert.False(Row(vm, "PORT").ReferencesUnknownInput);

        // declaring the input recolours it (the overlay watches the live variable list)
        vars.Add("apiHost");
        Assert.False(Row(vm, "HOST").ReferencesUnknownInput);
        Assert.False(vm.Overrides.Single(o => o.Key == "HOST").ReferencesUnknownInput);
    }

    [Fact]
    public void A_needed_capabilitys_output_is_accepted_by_its_head_not_flagged()
    {
        var vars = new ObservableCollection<string> { "workspace" };
        var open = new ObservableCollection<string>();   // no needs declared yet
        var vm = new EnvOverlayViewModel(["DB"], NoExamples, variables: vars, openCapabilities: open);

        // ${sprig.db.connString} — the output lives in another repo. Undeclared until 'db' is a need.
        var db = Row(vm, "DB");
        db.Draft = "${sprig.db.connString}";
        vm.ApplyCommand.Execute(db);
        Assert.True(Row(vm, "DB").ReferencesUnknownInput);

        // declaring the need (its capability head) greens it, whatever the output — the overlay watches
        // the live open-capability list too.
        open.Add("db");
        Assert.False(Row(vm, "DB").ReferencesUnknownInput);

        // an unknown capability's output is still flagged.
        db.Draft = "${sprig.cache.url}";
        vm.ApplyCommand.Execute(db);
        Assert.True(Row(vm, "DB").ReferencesUnknownInput);
    }
}
