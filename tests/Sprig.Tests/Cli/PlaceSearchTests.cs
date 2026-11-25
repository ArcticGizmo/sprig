using Sprig.Cli.Commands;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

public class PlaceSearchTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "place-search-tests");

    static WorkspaceRecord Slot(string id, string name, string? branch, WorkspaceState state = WorkspaceState.Up)
        => new(id, $"{id}/repo.json", name, branch, "stable", [], [], [], state, DateTimeOffset.Now, DateTimeOffset.Now,
            Folder: Path.Combine(Root, $"{id}--{name}"));

    static readonly WorkspaceRecord WebAuth = Slot("shop-web", "1", "feature/auth-login");
    static readonly WorkspaceRecord WebCart = Slot("shop-web", "2", "feature/cart");
    static readonly WorkspaceRecord ApiAuth = Slot("shop-api", "1", "feature/auth-tokens");
    static readonly WorkspaceRecord ApiFree = Slot("shop-api", "2", null, WorkspaceState.Released);
    static readonly WorkspaceRecord[] All = [WebAuth, WebCart, ApiAuth, ApiFree];

    [Fact]
    public void A_word_matches_part_of_a_branch_in_any_case()
        => Assert.Equal([WebAuth, ApiAuth], PlaceTarget.Matching(All, "AUTH"));

    [Fact]
    public void Every_word_has_to_match_in_any_order()
        => Assert.Equal([ApiAuth], PlaceTarget.Matching(All, "auth  api"));

    [Fact]
    public void The_id_and_the_slots_folder_name_match_too()
    {
        Assert.Equal([WebAuth, WebCart], PlaceTarget.Matching(All, "shop-web"));
        Assert.Equal([WebCart], PlaceTarget.Matching(All, "shop-web--2"));
    }

    [Fact]
    public void Free_finds_the_free_slots()
        => Assert.Equal([ApiFree], PlaceTarget.Matching(All, "free"));

    [Fact]
    public void Nothing_matching_is_an_empty_list()
        => Assert.Empty(PlaceTarget.Matching(All, "billing"));
}
