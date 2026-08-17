using Sprig.Core.Config;
using Sprig.Core.Maps;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Maps;

public class CapabilityResolverTests
{
    // ---- builders -------------------------------------------------------------------------------

    static ResolvedRepo Repo(string name, params ModuleDeclaration[] modules)
        => new(name, $"/src/{name}", new SprigRepoConfig { Name = name, Modules = modules });

    static ModuleDeclaration Mod(string name, IReadOnlyList<ProvidedCapability>? provides = null, IReadOnlyList<Need>? needs = null)
        => new() { Name = name, Provides = provides ?? [], Needs = needs ?? [] };

    static ProvidedCapability Cap(string cap, params (string, OutputSpec)[] outputs)
        => new() { Capability = cap, Outputs = outputs.ToDictionary(o => o.Item1, o => o.Item2) };

    static IReadOnlyDictionary<string, int> Allocate(params ResolvedRepo[] repos)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        var n = 8000;
        foreach (var r in CapabilityResolver.PortRequests(repos))
            d[r.Name] = n++;
        return d;
    }

    static string Resolve(ResolvedModule m, string reference)
        => SubstitutionEngine.Resolve("${sprig." + reference + "}", m.Scope);

    static ResolvedModule Module(ResolvedWorkspace rw, string module) => rw.Modules.Single(m => m.Module == module);

    // A postgres-shaped provider: a port + a connString derived from it.
    static ProvidedCapability Postgres(string cap)
        => Cap(cap, ("port", OutputSpec.Port()),
                    ("connString", OutputSpec.Derived($"Host=localhost;Port=${{sprig.{cap}.port}};Database=app")));

    // An http-shaped provider: a port + a url derived from it.
    static ProvidedCapability Http(string cap)
        => Cap(cap, ("port", OutputSpec.Port()),
                    ("url", OutputSpec.Derived($"http://localhost:${{sprig.{cap}.port}}")));

    // ---- tests ----------------------------------------------------------------------------------

    [Fact]
    public void Monorepo_wires_and_resolves_locally_nothing_bubbles_out()
    {
        var acme = Repo("acme",
            Mod("api", provides: [Http("acme-api")], needs: [new Need { Capability = "acme-db" }]),
            Mod("web", needs: [new Need { Capability = "acme-api" }]),
            Mod("db", provides: [Postgres("acme-db")]));
        var ports = Allocate(acme);

        var rw = CapabilityResolver.Resolve("ws", null, [acme], ports);

        Assert.Empty(rw.Unsatisfied);
        var apiPort = ports["acme.acme-api.port"];
        var dbPort = ports["acme.acme-db.port"];
        // web reads the api's URL (derived from the api's own allocated port), resolved locally.
        Assert.Equal($"http://localhost:{apiPort}", Resolve(Module(rw, "web"), "acme-api.url"));
        // api reads the db's connString (local db module), also nearest-wins.
        Assert.Equal($"Host=localhost;Port={dbPort};Database=app", Resolve(Module(rw, "api"), "acme-db.connString"));
    }

    [Fact]
    public void A_need_bubbles_up_to_a_provider_in_another_repo()
    {
        var web = Repo("web", Mod("app", needs: [new Need { Capability = "api" }]));
        var api = Repo("api", Mod("app", provides: [Http("api")]));
        var ports = Allocate(web, api);

        var rw = CapabilityResolver.Resolve("ws", null, [web, api], ports);

        Assert.Empty(rw.Unsatisfied);
        Assert.Equal($"http://localhost:{ports["api.api.port"]}", Resolve(rw.Modules.Single(m => m.Repo == "web"), "api.url"));
    }

    [Fact]
    public void Nearest_wins_a_local_sibling_beats_a_remote_provider_of_the_same_capability()
    {
        var app = Repo("app",
            Mod("svc", provides: [Http("svc")]),
            Mod("consumer", needs: [new Need { Capability = "svc" }]));
        var other = Repo("other", Mod("svc", provides: [Http("svc")]));
        var ports = Allocate(app, other);

        var rw = CapabilityResolver.Resolve("ws", null, [app, other], ports);

        // The consumer's "svc.url" must point at the LOCAL app.svc port, not other.svc.
        Assert.Equal($"http://localhost:{ports["app.svc.port"]}", Resolve(Module(rw, "consumer"), "svc.url"));
    }

    [Fact]
    public void Two_remote_providers_of_one_capability_is_an_ambiguity_error()
    {
        var a = Repo("a", Mod("app", provides: [Http("dup")]));
        var b = Repo("b", Mod("app", provides: [Http("dup")]));
        var c = Repo("c", Mod("app", needs: [new Need { Capability = "dup" }]));
        var ports = Allocate(a, b, c);

        var ex = Assert.Throws<MapResolutionException>(() => CapabilityResolver.Resolve("ws", null, [a, b, c], ports));
        Assert.Contains("2 repos provide", ex.Message);
    }

    [Fact]
    public void Map_wiring_bridges_a_generically_named_need_to_a_specific_provider()
    {
        var web = Repo("web", Mod("app", needs: [new Need { Capability = "backend" }]));
        var api = Repo("orders", Mod("app", provides: [Http("orders-api")]));
        var ports = Allocate(web, api);
        var map = new MapDefinition
        {
            Name = "m",
            Repos = [MapRepo.Local("web"), MapRepo.Local("orders")],
            Wiring = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["web"] = new Dictionary<string, string> { ["backend"] = "orders-api" },
            },
        };

        var rw = CapabilityResolver.Resolve("ws", map, [web, api], ports);

        Assert.Empty(rw.Unsatisfied);
        // The need's alias defaults to its own capability name ("backend"), pointing at orders-api's outputs.
        Assert.Equal($"http://localhost:{ports["orders.orders-api.port"]}", Resolve(rw.Modules.Single(m => m.Repo == "web"), "backend.url"));
    }

    [Fact]
    public void A_default_fills_a_need_whose_provider_is_not_selected()
    {
        var web = Repo("web", Mod("app", needs: [new Need { Capability = "auth" }]));
        var map = new MapDefinition
        {
            Name = "m",
            Repos = [MapRepo.Local("web")],
            Defaults = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
            {
                ["web"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["auth"] = new Dictionary<string, string> { ["url"] = "https://auth.staging" },
                },
            },
        };

        var rw = CapabilityResolver.Resolve("ws", map, [web], Allocate(web));

        Assert.Empty(rw.Unsatisfied);
        Assert.Equal("https://auth.staging", Resolve(Module(rw, "app"), "auth.url"));
    }

    [Fact]
    public void An_inline_literal_fills_a_gap_and_wins_over_reporting()
    {
        var web = Repo("web", Mod("app", needs: [new Need { Capability = "auth" }]));
        var inline = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
        {
            ["web"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["auth"] = new Dictionary<string, string> { ["url"] = "http://localhost:9999" },
            },
        };

        var rw = CapabilityResolver.Resolve("ws", null, [web], Allocate(web), inline);

        Assert.Empty(rw.Unsatisfied);
        Assert.Equal("http://localhost:9999", Resolve(Module(rw, "app"), "auth.url"));
    }

    [Fact]
    public void An_unmet_need_is_reported_not_thrown()
    {
        var web = Repo("web", Mod("app", needs: [new Need { Capability = "auth", As = "authz" }]));

        var rw = CapabilityResolver.Resolve("ws", null, [web], Allocate(web));

        var gap = Assert.Single(rw.Unsatisfied);
        Assert.Equal("web", gap.Repo);
        Assert.Equal("app", gap.Module);
        Assert.Equal("auth", gap.Capability);
    }

    [Fact]
    public void Ports_are_allocated_for_every_provider_even_an_unconsumed_one()
    {
        var repo = Repo("solo", Mod("app", provides: [Http("lonely")]));

        var requests = CapabilityResolver.PortRequests([repo]);
        Assert.Equal("solo.lonely.port", Assert.Single(requests).Name);

        var rw = CapabilityResolver.Resolve("ws", null, [repo], Allocate(repo));
        Assert.Contains("solo.lonely.port", rw.Ports.Keys);
    }

    [Fact]
    public void A_ports_allowed_set_flows_into_the_request()
    {
        var repo = Repo("a", Mod("app", provides: [Cap("api", ("port", OutputSpec.Port("8100-8103")))]));
        var request = Assert.Single(CapabilityResolver.PortRequests([repo]));
        Assert.NotNull(request.Allowed);
        Assert.Equal([8100, 8101, 8102, 8103], request.Allowed!.OrderBy(p => p));
    }

    [Fact]
    public void The_as_alias_names_the_wired_provider_locally()
    {
        var app = Repo("app",
            Mod("api", provides: [Postgres("app-db")], needs: [new Need { Capability = "app-db", As = "primary" }]));
        var ports = Allocate(app);

        var rw = CapabilityResolver.Resolve("ws", null, [app], ports);

        // Referenced under the alias, not the capability name.
        Assert.Equal($"Host=localhost;Port={ports["app.app-db.port"]};Database=app", Resolve(Module(rw, "api"), "primary.connString"));
    }

    [Fact]
    public void A_self_referential_derived_output_is_a_cycle_error()
    {
        var repo = Repo("a", Mod("app", provides: [Cap("api", ("loop", OutputSpec.Derived("${sprig.api.loop}")))]));
        Assert.Throws<SubstitutionException>(() => CapabilityResolver.Resolve("ws", null, [repo], Allocate(repo)));
    }
}
