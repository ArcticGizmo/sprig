using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Definitions;

namespace Sprig.Tests.App;

public class CodeReviewViewModelTests
{
    static PendingTrustItem Pending(string path)
    {
        var sha = DefinitionHash.Of(File.ReadAllText(path));
        return new PendingTrustItem(path.Replace('\\', '/'), sha, false, path);
    }

    static string Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Each_file_is_shown_and_approved_on_its_own_and_the_last_approval_carries_on()
    {
        using var store = new TempStore();
        var web = Write(store.Root, "shop-web/sprig.yaml", "// web");
        var api = Write(store.Root, "shop-services/sprig.yaml", "// api");
        var services = new AppServices(store.Root);
        var carriedOn = 0;
        var review = new CodeReviewViewModel(services.Trust) { AllApproved = () => { carriedOn++; return Task.CompletedTask; } };

        review.Load([Pending(web), Pending(api)]);

        Assert.Equal(["shop-web/sprig.yaml", "shop-services/sprig.yaml"], review.Files.Select(f => f.Name));
        Assert.Equal("// web", review.Text);
        Assert.Equal("Approve shop-web/sprig.yaml", review.ApproveLabel);
        Assert.Equal("Approve all 2", review.ApproveAllLabel);

        await review.ApproveCurrentCommand.ExecuteAsync(null);

        Assert.Equal(TrustState.Trusted, services.Trust.Check(web, DefinitionHash.Of("// web")).State);
        Assert.Equal(TrustState.Unknown, services.Trust.Check(api, DefinitionHash.Of("// api")).State);
        Assert.Equal("// api", review.Text);
        Assert.Equal(1, review.Remaining);
        Assert.False(review.ShowApproveAll);
        Assert.Equal(0, carriedOn);

        await review.ApproveCurrentCommand.ExecuteAsync(null);

        Assert.Equal(0, review.Remaining);
        Assert.Equal(1, carriedOn);
    }

    [Fact]
    public async Task The_dialog_closes_once_all_is_approved_but_stays_open_for_files_that_turn_up_next()
    {
        using var store = new TempStore();
        var orchestration = Write(store.Root, "storefront.orchestration.yaml", "// orchestration");
        var web = Write(store.Root, "shop-web/sprig.yaml", "// web");
        var review = new CodeReviewViewModel(new AppServices(store.Root).Trust);
        var rounds = new Queue<PendingTrustItem[]>([[Pending(web)], []]);
        review.AllApproved = () => { review.Load(rounds.Dequeue()); return Task.CompletedTask; };

        review.Load([Pending(orchestration)]);
        review.OpenCommand.Execute(null);
        await review.ApproveCurrentCommand.ExecuteAsync(null);

        Assert.True(review.IsOpen);
        Assert.Equal("shop-web/sprig.yaml", review.Current?.Name);

        await review.ApproveCurrentCommand.ExecuteAsync(null);

        Assert.False(review.IsOpen);
    }

    [Fact]
    public void A_file_that_changed_after_it_was_checked_cannot_be_approved_until_it_is_checked_again()
    {
        using var store = new TempStore();
        var web = Write(store.Root, "shop-web/sprig.yaml", "// web");
        var pending = Pending(web);
        File.WriteAllText(web, "// web, edited since");
        var review = new CodeReviewViewModel(new AppServices(store.Root).Trust);

        review.Load([pending]);

        Assert.False(review.CanApproveCurrent);
        Assert.Contains("changed after sprig checked it", review.Problem);
    }
}
