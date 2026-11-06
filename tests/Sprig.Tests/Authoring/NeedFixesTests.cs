using Sprig.Engine.Authoring;

namespace Sprig.Tests.Authoring;

public class NeedFixesTests
{
    const string Storefront = """
        #:package Sprig.Sdk@0.1.0
        using Sprig;

        var orchestration = Orchestration.Describe("storefront");

        var services = orchestration.UseRepo("shop-services");   // the APIs
        orchestration.UseRepo("shop-web");

        return orchestration.Emit(args);
        """;

    static NeedFix Fix(string text, UnfilledNeed need, string label) => NeedFixes.For(text, need).Single(f => f.Label == label);

    [Fact]
    public void A_mail_need_gets_a_mailpit_and_a_binding_after_the_repo_lines_and_always_a_value_to_fill_in()
    {
        var fixes = NeedFixes.For(Storefront, new UnfilledNeed("mail-smtp", "url", []));

        Assert.Equal(["Add Mailpit to the orchestration", "Set it to a value"], fixes.Select(f => f.Label));
        var mail = fixes[0];
        Assert.True(mail.Placed);
        Assert.Contains("""
            orchestration.UseRepo("shop-web");

            var mail = orchestration.Mailpit("mail");
            orchestration.Bind("mail-smtp", mail.SmtpUrl);

            return orchestration.Emit(args);
            """.ReplaceLineEndings("\n"), mail.Text.ReplaceLineEndings("\n"));
        Assert.Contains("var services = orchestration.UseRepo(\"shop-services\");   // the APIs", mail.Text);
        Assert.Contains("orchestration.Bind(\"mail-smtp\", \"\");", fixes[1].Text);
    }

    [Fact]
    public void Infra_the_orchestration_already_runs_is_reused_and_a_new_binding_joins_the_others()
    {
        var text = Storefront.Replace("orchestration.UseRepo(\"shop-web\");", """
            orchestration.UseRepo("shop-web");

            var mail = orchestration.Mailpit("mail");
            orchestration.Bind("mail-smtp", mail.SmtpUrl);
            """);

        var fix = Fix(text, new UnfilledNeed("notify-smtp", "url", []), "Use the orchestration's mail");

        Assert.Contains("orchestration.Bind(\"mail-smtp\", mail.SmtpUrl);\norchestration.Bind(\"notify-smtp\", mail.SmtpUrl);", fix.Text.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("mail2", fix.Text);
    }

    [Fact]
    public void A_database_need_gets_its_own_database_and_a_name_already_taken_gets_a_number()
    {
        var text = Storefront.Replace("var services", "var pg = 1;\nvar services");

        var fix = Fix(text, new UnfilledNeed("orders-db", "connectionstring", []), "Add Postgres to the orchestration");

        Assert.Contains("var pg2 = orchestration.Postgres(\"pg2\");", fix.Text);
        Assert.Contains("orchestration.Bind(\"orders-db\", pg2.Database(\"orders\"));", fix.Text);
    }

    [Fact]
    public void When_several_repos_offer_it_each_can_be_picked_by_its_variable_and_one_without_gets_one()
    {
        var fixes = NeedFixes.For(Storefront, new UnfilledNeed("catalog-url", "url", ["shop-services", "shop-web"]));

        Assert.Equal(["Use shop-services's", "Use shop-web's", "Set it to a value"], fixes.Select(f => f.Label));
        Assert.Contains("orchestration.Bind(\"catalog-url\", services.Export(\"catalog-url\"));", fixes[0].Text);
        Assert.Contains("var shopWeb = orchestration.UseRepo(\"shop-web\");", fixes[1].Text);
        Assert.Contains("orchestration.Bind(\"catalog-url\", shopWeb.Export(\"catalog-url\"));", fixes[1].Text);
    }

    [Fact]
    public void A_file_it_cannot_place_the_lines_in_still_offers_them_to_paste()
    {
        const string odd = "using Sprig;\nreturn Build().Emit(args);\nstatic OrchestrationBuilder Build() => Orchestration.Describe(\"x\");\n";
        var fix = NeedFixes.For(odd.Replace("Orchestration.Describe", "Make"), new UnfilledNeed("mail-smtp", "url", []))[0];

        Assert.False(fix.Placed);
        Assert.Contains("Describe", fix.Error);
        Assert.Equal(["var mail = orchestration.Mailpit(\"mail\");", "orchestration.Bind(\"mail-smtp\", mail.SmtpUrl);"], fix.Lines);

        var bound = NeedFixes.For(Storefront.Replace("orchestration.UseRepo(\"shop-web\");", "orchestration.UseRepo(\"shop-web\");\norchestration.Bind(\"mail-smtp\", \"x\");"),
            new UnfilledNeed("mail-smtp", "url", []));
        Assert.All(bound, f => Assert.Contains("already binds", f.Error));
    }
}
