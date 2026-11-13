namespace Sprig.App.Authoring;

/// <summary>A small but realistic repo definition: what the headless editor renders and the language service's tests
/// read and complete against.</summary>
public static class SampleDefinition
{
    public const string Text = """
        schema: 1
        repo: shop-services

        containers:
          pg: { preset: postgres }

        inputs:
          mail-smtp:
            type: url
            default: smtp://localhost:1025

        apps:
          # The catalog owns stock levels.
          catalog-api:
            run: dotnet
            path: src/Catalog.Api
            ports: { http: any }
            wait-for: [pg]

          orders-api:
            run: dotnet
            path: src/Orders.Api
            ports: { http: any }
            wait-for: [pg, catalog-api]

        files:
          src/Catalog.Api/appsettings.Local.json:
            read-by: [catalog-api]
            values:
              Urls: ${catalog-api.url}
              ConnectionStrings:
                Db: Host=localhost;Port=${pg.port};Database=catalog

          src/Orders.Api/appsettings.Local.json:
            read-by: [orders-api]
            values:
              Urls: ${orders-api.url}
              Smtp:
                Url: ${inputs.mail-smtp}

        outputs:
          catalog-url:
            type: url
            value: ${catalog-api.url}
        """;
}
