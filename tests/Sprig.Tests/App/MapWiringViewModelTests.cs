using Sprig.App;
using Sprig.App.Maps;
using Sprig.App.Rendering;
using Sprig.App.ViewModels;
using Sprig.Core.Config;
using Sprig.Core.Maps;

namespace Sprig.Tests.App;

/// <summary>The wiring section of the Maps page, over the invented storefront map in a throwaway store.</summary>
public class MapWiringViewModelTests
{
    static (TempStore Store, AppServices Services, MapsViewModel Maps) Storefront()
    {
        var store = new TempStore();
        var services = new AppServices(store.Root);
        foreach (var repo in WiringExample.Repos)
        {
            var dir = Path.Combine(store.Root, "repos", repo.Name);
            SprigConfigWriter.Write(repo.Config, Path.Combine(dir, ".sprig.json"));
            services.Repos.Add(dir);
        }
        Directory.CreateDirectory(Path.Combine(store.Root, "repos", "shop", "apps", "web"));
        File.WriteAllText(Path.Combine(store.Root, "repos", "shop", "apps", "web", ".env.template"), "VITE_HELP_URL=http://localhost:4000/\n");
        services.Maps.Save(WiringExample.Map);
        return (store, services, new MapsViewModel(services));
    }

    [Fact]
    public void Selecting_a_map_shows_what_blocks_checkout_and_selects_the_first_unmet_need()
    {
        var (store, _, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;

        Assert.False(wiring.IsReady);
        Assert.StartsWith("3 needs can't be met", wiring.Headline);
        Assert.Equal(3, wiring.Blockers.Count);
        Assert.Contains("most relied on: auth › app (3 modules)", wiring.Counts);
        Assert.StartsWith("G:", wiring.SelectedNodeId);
        Assert.Equal("Unmet need", wiring.SelectionKind);
        Assert.Single(wiring.FixedValues);
        Assert.Equal(["needs a value", "needs a value", "pick one"], wiring.Ledger.Take(3).Select(r => r.Status));
    }

    [Fact]
    public void Setting_a_value_from_the_hint_turns_the_gap_into_a_constant_and_keeps_it_selected()
    {
        var (store, services, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;
        wiring.SelectedNodeId = "G:shop/web/help-site";
        var need = wiring.SelectedNeeds.Single();
        Assert.Equal(("url", "http://localhost:4000"), (need.Output, need.Value));

        need.SetConstantCommand.Execute(null);

        Assert.Null(wiring.Error);
        Assert.Equal("D:shop/web/help-site", wiring.SelectedNodeId);
        Assert.Equal("Constant set on the map", wiring.SelectionKind);
        Assert.Equal("http://localhost:4000", services.Maps.Get("storefront")!.Defaults["shop"]["help-site"]["url"]);
        Assert.StartsWith("2 needs", wiring.Headline);
    }

    [Fact]
    public void Removing_a_constant_brings_the_gap_back()
    {
        var (store, services, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;
        wiring.SelectedNodeId = "D:billing/app/payments-gateway";

        wiring.SelectedNeeds.Single().RemoveConstantCommand.Execute(null);

        Assert.Equal("G:billing/app/payments-gateway", wiring.SelectedNodeId);
        Assert.Empty(services.Maps.Get("storefront")!.Defaults);
    }

    [Fact]
    public void Picking_a_provider_settles_the_ambiguity_with_a_repo_wiring_entry()
    {
        var (store, services, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;
        wiring.SelectedNodeId = MapGraphLayout.ModuleId("shop", "api");
        var cache = wiring.SelectedNeeds.Single(n => n.Capability == "cache");

        cache.Providers.Single(p => p.Label == "Use billing").PickCommand.Execute(null);

        Assert.Equal("billing/cache", services.Maps.Get("storefront")!.Wiring["shop"]["cache"]);
        Assert.Empty(wiring.Inspection!.Ambiguities);
        var wired = wiring.SelectedNeeds.Single(n => n.Capability == "cache");
        Assert.True(wired.IsWiredByEntry);

        wired.UnwireCommand.Execute(null);
        Assert.Single(wiring.Inspection!.Ambiguities);
    }

    [Fact]
    public void Wiring_a_near_miss_to_its_lookalike_meets_the_need()
    {
        var (store, _, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;
        wiring.SelectedNodeId = "G:admin/app/auth_api";

        wiring.SelectedNeeds.Single().WireSimilarCommand.Execute(null);

        Assert.DoesNotContain(wiring.Inspection!.Gaps, n => n.Repo == "admin");
        Assert.Equal(MapGraphLayout.ModuleId("admin", "app"), wiring.SelectedNodeId);
    }

    [Fact]
    public void Converting_a_fixed_value_edits_the_repo_config_and_selects_the_new_constant()
    {
        var (store, _, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;
        var row = wiring.FixedValues.Single();
        Assert.Equal(("mail", "address"), (row.Capability, row.Output));

        row.ConvertCommand.Execute(null);

        Assert.Null(wiring.Error);
        Assert.Empty(wiring.FixedValues);
        Assert.Equal("D:shop/api/mail", wiring.SelectedNodeId);
        var config = SprigConfigLoader.LoadFromFile(Path.Combine(store.Root, "repos", "shop", ".sprig.json"));
        Assert.Equal("${sprig.mail.address}", config.Modules.Single(m => m.Name == "api").Env.Single().Set["MAIL_HOST"]);
    }

    [Fact]
    public void A_refused_fix_shows_why_and_changes_nothing()
    {
        var (store, services, maps) = Storefront();
        using var _ = store;
        var wiring = maps.Wiring;
        wiring.SelectedNodeId = "G:shop/web/help-site";
        var need = wiring.SelectedNeeds.Single();
        need.Value = "  ";

        need.SetConstantCommand.Execute(null);

        Assert.Contains("empty", wiring.Error);
        Assert.False(services.Maps.Get("storefront")!.Defaults.ContainsKey("shop"));
    }

    [Theory]
    [InlineData("MAIL_HOST", "mail")]
    [InlineData("RABBITMQ_PORT", "rabbitmq")]
    [InlineData("ConnectionStrings__Queue", "queue")]
    [InlineData("SMTP", "smtp")]
    public void A_fixed_values_key_suggests_a_need_name(string key, string expected)
        => Assert.Equal(expected, FixedValueRow.SuggestCapability(key));
}
