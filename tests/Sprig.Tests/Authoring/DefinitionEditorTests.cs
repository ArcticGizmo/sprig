using Sprig.Engine.Authoring;

namespace Sprig.Tests.Authoring;

public class DefinitionEditorTests
{
    const string Source = """
        #:package Sprig.Sdk@0.1.0
        using Sprig;

        var repo = Repo.Describe("shop-services");

        var smtp = repo.Import<Url>("mail-smtp");                // filled in by the system

        // The catalog owns stock levels.
        var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
            .Http("http")
            .Env("Mode",   "dev")      // aligned on purpose
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
    public void AddEnv_goes_after_the_last_env_with_the_chains_indent_and_touches_nothing_else()
    {
        var result = DefinitionEditor.AddEnv(Source, "catalog-api", "Smtp__Url", "smtp");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Source.Replace(
            """
                .Env("Mode",   "dev")      // aligned on purpose
            """,
            """
                .Env("Mode",   "dev")      // aligned on purpose
                .Env("Smtp__Url", smtp)
            """), result.Text);
    }

    [Fact]
    public void AddEnv_on_a_one_line_chain_starts_a_continuation_line()
    {
        var result = DefinitionEditor.AddEnv(Source, "web", "VITE_API_URL", "catalog.Url");

        Assert.True(result.Ok, result.Error);
        Assert.Contains("""
            var web = repo.Node("web", "apps/web").Http()
                .Env("VITE_API_URL", catalog.Url);
            """, result.Text);
    }

    [Fact]
    public void AddEnv_refuses_a_key_that_is_already_set_and_an_app_inside_a_loop()
    {
        Assert.Contains("already sets Mode", DefinitionEditor.AddEnv(Source, "catalog-api", "Mode", "\"x\"").Error);
        Assert.Contains("no editable app", DefinitionEditor.AddEnv(Source, "a", "K", "\"x\"").Error);
    }

    [Fact]
    public void AddEnv_that_would_not_compile_is_refused_and_the_text_is_unchanged()
    {
        var result = DefinitionEditor.AddEnv(Source, "catalog-api", "Broken", "(");

        Assert.False(result.Ok);
        Assert.Equal(Source, result.Text);
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
    public void AddImport_goes_under_the_last_import_and_keeps_its_comment()
    {
        var result = DefinitionEditor.AddImport(Source, "billing-url", "Url", "billing");

        Assert.True(result.Ok, result.Error);
        Assert.Contains("""
            var smtp = repo.Import<Url>("mail-smtp");                // filled in by the system
            var billing = repo.Import<Url>("billing-url");
            """, result.Text);
    }

    [Fact]
    public void AddImport_refuses_duplicates()
    {
        Assert.Contains("already imports", DefinitionEditor.AddImport(Source, "mail-smtp", "Url", "x").Error);
        Assert.Contains("already exists", DefinitionEditor.AddImport(Source, "new", "Url", "catalog").Error);
    }

    [Fact]
    public void Crlf_files_stay_crlf()
    {
        var crlf = Source.Replace("\n", "\r\n");

        var result = DefinitionEditor.AddEnv(crlf, "catalog-api", "K", "\"v\"");

        Assert.DoesNotContain("\n", result.Text.Replace("\r\n", ""));
    }
}
