using Sprig.Engine.Store;

namespace Sprig.Tests.Store;

public class AppProfileTests
{
    [Fact]
    public void A_dev_build_keeps_a_store_of_its_own_beside_the_installed_apps()
    {
        Assert.Equal(AppProfile.IsDev ? "sprig (Dev)" : "sprig", AppProfile.DataFolderName);
    }

    [Fact]
    public void The_old_apps_ports_file_in_the_same_folder_counts_as_empty_and_is_replaced()
    {
        using var store = new TempStore();
        Directory.CreateDirectory(store.Paths.Root);
        File.WriteAllText(Path.Combine(store.Paths.Root, "ports.json"), """{ "Leases": { "@shared/postgres-17": { "port": 8003 } } }""");
        var leases = new Sprig.Engine.Workspaces.PortLeases(store.Paths);

        Assert.Empty(leases.All());
        leases.Update("shop-web/main", (mine, others) => (0, new Dictionary<string, int> { ["shop-web/web.http"] = 9170 }));
        Assert.Equal(9170, leases.Get("shop-web/main")["shop-web/web.http"]);
    }
}
