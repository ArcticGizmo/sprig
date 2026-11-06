using Sprig.App.ViewModels;
using Sprig.Engine.Authoring;

namespace Sprig.Tests.App;

public sealed class FillNeedsViewModelTests : IDisposable
{
    readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    string Orchestration()
    {
        Directory.CreateDirectory(_store.Root);
        var file = Path.Combine(_store.Root, "storefront.orchestration.cs");
        File.WriteAllText(file, "using Sprig;\n\nvar orchestration = Orchestration.Describe(\"storefront\");\n\norchestration.UseRepo(\"shop-services\");\n\nreturn orchestration.Emit(args);\n");
        return file;
    }

    [Fact]
    public async Task It_shows_the_first_need_with_the_likeliest_fix_as_a_diff_and_writes_only_once_asked()
    {
        var file = Orchestration();
        string? written = null;
        var opened = 0;
        var vm = new FillNeedsViewModel { Apply = (_, text) => { written = text; return Task.CompletedTask; }, OpenCode = () => opened++ };

        vm.Open(file, [("shop-services", new UnfilledNeed("mail-smtp", "url", [])), ("shop-web", new UnfilledNeed("api-url", "url", []))]);

        Assert.True(vm.IsOpen);
        Assert.Equal("Fill mail-smtp", vm.Title);
        Assert.Contains("shop-services needs mail-smtp (a url)", vm.Subtitle);
        Assert.Equal("1 more after this one.", vm.More);
        Assert.Equal("Add Mailpit to the orchestration", vm.Selected!.Label);
        Assert.True(vm.Selected.IsSelected);
        Assert.Equal(["+", "+", "+"], vm.Diff.Where(d => d.Mark != " ").Select(d => d.Mark));
        Assert.Contains(vm.Diff, d => d.IsAdded && d.Text == "var mail = orchestration.Mailpit(\"mail\");");
        Assert.Contains(vm.Diff, d => d.Mark == " " && d.Text == "orchestration.UseRepo(\"shop-services\");");
        Assert.Null(written);

        await vm.AddToCodeCommand.ExecuteAsync(null);
        Assert.False(vm.IsOpen);
        Assert.Contains("orchestration.Bind(\"mail-smtp\", mail.SmtpUrl);", written);
        Assert.Equal(0, opened);

        // A value to fill in opens the code once it's written.
        vm.Open(file, [("shop-web", new UnfilledNeed("api-url", "url", []))]);
        vm.PickCommand.Execute(vm.Options.Single(o => o.Label == "Set it to a value"));
        await vm.AddToCodeCommand.ExecuteAsync(null);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void A_diff_shows_a_changed_line_as_taken_out_and_put_back_with_two_lines_around()
    {
        var lines = FillNeedsViewModel.Changes("a\nb\nc\nd\ne\nf", "a\nb\nc\nX\nd2\ne\nf");

        Assert.Equal([" b", " c", "-d", "+X", "+d2", " e", " f"], lines.Select(l => l.Mark + l.Text));
    }
}
