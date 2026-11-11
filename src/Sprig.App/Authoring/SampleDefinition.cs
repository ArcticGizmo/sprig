namespace Sprig.App.Authoring;

/// <summary>A small but realistic repo definition: what the headless editor render and the language host's tests read
/// and complete against.</summary>
public static class SampleDefinition
{
    public const string Text = """
        #:package ArcticGizmo.Sprig.Sdk@0.9.0
        using Sprig;

        var repo = Repo.Describe("shop-services");

        var pg = repo.Container("pg", "postgres:17")
            .Endpoint("tcp", targetPort: 5432);

        var smtp = repo.Setting<Url>("mail-smtp", "smtp://localhost:1025");

        // The catalog owns stock levels.
        var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
            .Http("http")
            .WaitFor(pg);

        catalog.AppSettings("appsettings.Local.json", s => s
            .Set("Urls", catalog.Url)
            .Set("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog"));

        var orders = repo.DotNet("orders-api", "src/Orders.Api")
            .Http("http")
            .WaitFor(pg, catalog);

        orders.AppSettings("appsettings.Local.json", s => s
            .Set("Urls", orders.Url)
            .Set("Smtp__Url", smtp));

        repo.Export<Url>("catalog-url", catalog.Url);

        return repo.Emit(args);
        """;
}
