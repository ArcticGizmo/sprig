using Sprig.Engine.Authoring;

namespace Sprig.Tests.Authoring;

public class NeedFixesTests
{
    const string Storefront = """
        #:package Sprig.Sdk@0.1.0
        using Sprig;

        var trellis = Trellis.Describe("storefront");

        var services = trellis.UseRepo("shop-services");   // the APIs
        trellis.UseRepo("shop-web");

        return trellis.Emit(args);
        """;

    static NeedFix Fix(string text, UnfilledNeed need, string label) => NeedFixes.For(text, need).Single(f => f.Label == label);

    [Fact]
    public void A_mail_need_gets_a_mailpit_and_a_binding_after_the_repo_lines_and_always_a_value_to_fill_in()
    {
        var fixes = NeedFixes.For(Storefront, new UnfilledNeed("mail-smtp", "url", []));

        Assert.Equal(["Add Mailpit to the trellis", "Set it to a value"], fixes.Select(f => f.Label));
        var mail = fixes[0];
        Assert.True(mail.Placed);
        Assert.Contains("""
            trellis.UseRepo("shop-web");

            var mail = trellis.Mailpit("mail");
            trellis.Bind("mail-smtp", mail.SmtpUrl);

            return trellis.Emit(args);
            """.ReplaceLineEndings("\n"), mail.Text.ReplaceLineEndings("\n"));
        Assert.Contains("var services = trellis.UseRepo(\"shop-services\");   // the APIs", mail.Text);
        Assert.Contains("trellis.Bind(\"mail-smtp\", \"\");", fixes[1].Text);
    }

    [Fact]
    public void Infra_the_trellis_already_runs_is_reused_and_a_new_binding_joins_the_others()
    {
        var text = Storefront.Replace("trellis.UseRepo(\"shop-web\");", """
            trellis.UseRepo("shop-web");

            var mail = trellis.Mailpit("mail");
            trellis.Bind("mail-smtp", mail.SmtpUrl);
            """);

        var fix = Fix(text, new UnfilledNeed("notify-smtp", "url", []), "Use the trellis's mail");

        Assert.Contains("trellis.Bind(\"mail-smtp\", mail.SmtpUrl);\ntrellis.Bind(\"notify-smtp\", mail.SmtpUrl);", fix.Text.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("mail2", fix.Text);
    }

    [Fact]
    public void A_database_need_gets_its_own_database_and_a_name_already_taken_gets_a_number()
    {
        var text = Storefront.Replace("var services", "var pg = 1;\nvar services");

        var fix = Fix(text, new UnfilledNeed("orders-db", "connectionstring", []), "Add Postgres to the trellis");

        Assert.Contains("var pg2 = trellis.Postgres(\"pg2\");", fix.Text);
        Assert.Contains("trellis.Bind(\"orders-db\", pg2.Database(\"orders\"));", fix.Text);
    }

    [Fact]
    public void When_several_repos_offer_it_each_can_be_picked_by_its_variable_and_one_without_gets_one()
    {
        var fixes = NeedFixes.For(Storefront, new UnfilledNeed("catalog-url", "url", ["shop-services", "shop-web"]));

        Assert.Equal(["Use shop-services's", "Use shop-web's", "Set it to a value"], fixes.Select(f => f.Label));
        Assert.Contains("trellis.Bind(\"catalog-url\", services.Export(\"catalog-url\"));", fixes[0].Text);
        Assert.Contains("var shopWeb = trellis.UseRepo(\"shop-web\");", fixes[1].Text);
        Assert.Contains("trellis.Bind(\"catalog-url\", shopWeb.Export(\"catalog-url\"));", fixes[1].Text);
    }

    [Fact]
    public void A_file_it_cannot_place_the_lines_in_still_offers_them_to_paste()
    {
        const string odd = "using Sprig;\nreturn Build().Emit(args);\nstatic TrellisBuilder Build() => Trellis.Describe(\"x\");\n";
        var fix = NeedFixes.For(odd.Replace("Trellis.Describe", "Make"), new UnfilledNeed("mail-smtp", "url", []))[0];

        Assert.False(fix.Placed);
        Assert.Contains("Describe", fix.Error);
        Assert.Equal(["var mail = trellis.Mailpit(\"mail\");", "trellis.Bind(\"mail-smtp\", mail.SmtpUrl);"], fix.Lines);

        var bound = NeedFixes.For(Storefront.Replace("trellis.UseRepo(\"shop-web\");", "trellis.UseRepo(\"shop-web\");\ntrellis.Bind(\"mail-smtp\", \"x\");"),
            new UnfilledNeed("mail-smtp", "url", []));
        Assert.All(bound, f => Assert.Contains("already binds", f.Error));
    }
}
