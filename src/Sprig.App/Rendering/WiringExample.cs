using Sprig.Core.Config;
using Sprig.Core.Maps;
using Sprig.Core.Workspaces;

namespace Sprig.App.Rendering;

/// <summary>
/// An invented map with one of everything the wiring view draws: a service three repos rely on, a monorepo
/// whose modules wire to each other, two repos providing the same capability, an unmet need with a
/// near-miss name, constants set on the map and provided by a module, and a fixed env value. Shared by the
/// headless render and the layout tests so both look at the same picture.
/// </summary>
public static class WiringExample
{
    public static MapDefinition Map { get; } = new()
    {
        Name = "storefront",
        Repos = [MapRepo.Local("shop"), MapRepo.Local("billing"), MapRepo.Local("catalog"), MapRepo.Local("auth"), MapRepo.Local("admin")],
        Defaults = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
        {
            ["billing"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["payments-gateway"] = new Dictionary<string, string> { ["url"] = "https://pay.sandbox.test" },
            },
        },
    };

    public static IReadOnlyList<ResolvedRepo> Repos { get; } =
    [
        Repo("shop", """
            { "schema": 1, "name": "shop", "modules": [
              { "name": "web", "path": "apps/web",
                "provides": [ { "capability": "shop-web", "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.shop-web.port}" } } ],
                "needs": [ { "capability": "shop-api" }, { "capability": "help-site" } ],
                "env": [ { "file": ".env.local", "set": {
                  "VITE_PORT": "${sprig.shop-web.port}", "VITE_API_URL": "${sprig.shop-api.url}", "VITE_HELP_URL": "${sprig.help-site.url}/" } } ] },
              { "name": "api", "path": "services/api",
                "provides": [ { "capability": "shop-api", "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.shop-api.port}" } } ],
                "needs": [ { "capability": "auth-api" }, { "capability": "billing-api" }, { "capability": "catalog-api" }, { "capability": "cache" } ],
                "env": [ { "file": ".env", "set": {
                  "PORT": "${sprig.shop-api.port}", "AUTH_URL": "${sprig.auth-api.url}", "BILLING_URL": "${sprig.billing-api.url}",
                  "CATALOG_URL": "${sprig.catalog-api.url}", "REDIS_PORT": "${sprig.cache.port}", "MAIL_HOST": "localhost:1025" } } ] } ] }
            """),
        Repo("billing", """
            { "schema": 1, "name": "billing",
              "provides": [
                { "capability": "billing-api", "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.billing-api.port}" } },
                { "capability": "cache", "ports": { "port": true } } ],
              "needs": [ { "capability": "auth-api" }, { "capability": "auth-realm" }, { "capability": "payments-gateway" } ],
              "env": [ { "file": ".env", "set": {
                "PORT": "${sprig.billing-api.port}", "REDIS_PORT": "${sprig.cache.port}", "AUTH_URL": "${sprig.auth-api.url}",
                "AUTH_REALM": "${sprig.auth-realm.realm}", "PAYMENTS_URL": "${sprig.payments-gateway.url}" } } ] }
            """),
        Repo("catalog", """
            { "schema": 1, "name": "catalog",
              "provides": [
                { "capability": "catalog-api", "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.catalog-api.port}" } },
                { "capability": "search", "ports": { "port": true } },
                { "capability": "cache", "ports": { "port": true } } ],
              "needs": [ { "capability": "auth-api" }, { "capability": "auth-realm" } ],
              "env": [ { "file": ".env", "set": {
                "PORT": "${sprig.catalog-api.port}", "SEARCH_PORT": "${sprig.search.port}", "REDIS_PORT": "${sprig.cache.port}",
                "AUTH_URL": "${sprig.auth-api.url}", "AUTH_REALM": "${sprig.auth-realm.realm}" } } ] }
            """),
        Repo("auth", """
            { "schema": 1, "name": "auth",
              "provides": [
                { "capability": "auth-api", "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.auth-api.port}" } },
                { "capability": "auth-realm", "shapes": { "realm": "storefront-dev" } },
                { "capability": "auth-db", "ports": { "port": true } } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.auth-api.port}", "DB_PORT": "${sprig.auth-db.port}" } } ] }
            """),
        Repo("admin", """
            { "schema": 1, "name": "admin",
              "provides": [ { "capability": "admin-ui", "ports": { "port": true } } ],
              "needs": [ { "capability": "auth_api" } ],
              "env": [ { "file": ".env.local", "set": { "VITE_PORT": "${sprig.admin-ui.port}", "VITE_AUTH_URL": "${sprig.auth_api.url}" } } ] }
            """),
    ];

    public static MapInspection Inspect() => MapInspector.Inspect(Map, Repos);

    static ResolvedRepo Repo(string name, string json) => new(name, $"/example/{name}", SprigConfigLoader.Parse(json));
}
