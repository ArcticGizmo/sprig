using Sprig.Engine.Authoring;

namespace Sprig.Tests.Authoring;

public class DefinitionEditorTests
{
    const string Source = """
        #:package ArcticGizmo.Sprig.Sdk@0.9.0
        using Sprig;

        var repo = Repo.Describe("shop-services");

        var smtp = repo.Setting<Url>("mail-smtp");                // asked for when a workspace is made

        // The catalog owns stock levels.
        var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
            .Http("http")
            .AppSettings("appsettings.Local.json", s => s.Set("Mode",   "dev"))      // aligned on purpose
            .WaitFor();

        var web = repo.Node("web", "apps/web").Http();

        foreach (var name in new[] { "a", "b" })
            repo.Node(name, name).Http();

        return repo.Emit(args);
        """;

    [Fact]
    public void Inspect_finds_every_app_and_marks_loops_and_computed_names_read_only()
    {
        var found = DefinitionEditor.Inspect(Source);

        Assert.Equal(["catalog-api", "web", "(computed)"], found.Select(f => f.Name));
        Assert.True(found[0].Editable);
        Assert.Equal("catalog", found[0].Variable);
        Assert.Equal(9, found[0].Line);
        Assert.False(found[2].Editable);
    }

    [Fact]
    public void SetPin_adds_changes_and_removes_the_named_argument()
    {
        var added = DefinitionEditor.SetPin(Source, "catalog-api", "http", "8100-8103");
        Assert.Contains(""".Http("http", pin: "8100-8103")""", added.Text);

        var changed = DefinitionEditor.SetPin(added.Text, "catalog-api", "http", "8200");
        Assert.Contains(""".Http("http", pin: "8200")""", changed.Text);

        var removed = DefinitionEditor.SetPin(changed.Text, "catalog-api", "http", null);
        Assert.Equal(Source, removed.Text);
    }

    [Fact]
    public void SetPin_on_an_endpoint_with_no_arguments_names_it()
    {
        var result = DefinitionEditor.SetPin(Source, "web", "http", "5170-5173");

        Assert.Contains(""".Http("http", pin: "5170-5173")""", result.Text);
    }

    [Fact]
    public void AddSetting_goes_under_the_last_setting_and_keeps_its_comment()
    {
        var result = DefinitionEditor.AddSetting(Source, "billing-url", "Url", "billing");

        Assert.True(result.Ok, result.Error);
        Assert.Contains("""
            var smtp = repo.Setting<Url>("mail-smtp");                // asked for when a workspace is made
            var billing = repo.Setting<Url>("billing-url");
            """, result.Text);
    }

    [Fact]
    public void AddSetting_refuses_duplicates()
    {
        Assert.Contains("already has a setting", DefinitionEditor.AddSetting(Source, "mail-smtp", "Url", "x").Error);
        Assert.Contains("already exists", DefinitionEditor.AddSetting(Source, "new", "Url", "catalog").Error);
    }

    [Fact]
    public void Crlf_files_stay_crlf()
    {
        var crlf = Source.Replace("\n", "\r\n");

        var result = DefinitionEditor.AddSetting(crlf, "billing-url", "Url", "billing");

        Assert.DoesNotContain("\n", result.Text.Replace("\r\n", ""));
    }
}
