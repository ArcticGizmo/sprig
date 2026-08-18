using Sprig.App;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the first-run comprehension fixes from docs/guided-tour-plan.md §11.2 — the changes that make
/// the wall of inputs smaller, so the coachmark script doesn't have to apologise for it.
/// </summary>
public class FirstRunFixTests
{
    // --- Scaffold notes -----------------------------------------------------

    [Fact]
    public async Task Adding_a_repo_without_a_config_explains_what_sprig_filled_in()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("needs-init");
        var services = new AppServices(s.Root);

        // An env file with a port-shaped key is exactly what InitInspector turns into a declared input.
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "PORT=4000\nNAME=x\n");
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), ".env\n");

        var vm = new ReposViewModel(services) { NewPath = repo.Path };
        await vm.ConfirmAddCommand.ExecuteAsync(null);

        // The notes are the CLI's long-standing explanation of the scaffold; the app used to discard them,
        // leaving a newcomer with a pre-filled form and no account of where any of it came from.
        Assert.True(vm.HasScaffoldNotes, "expected the scaffold explanation to be surfaced");
        Assert.NotEmpty(vm.ScaffoldNotes);

        vm.DismissScaffoldNotesCommand.Execute(null);
        Assert.False(vm.HasScaffoldNotes);
    }

    [Fact]
    public async Task Adding_a_repo_that_already_has_a_config_explains_nothing()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("has-config");
        var services = new AppServices(s.Root);

        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"),
            """{ "schema": 2, "name": "has-config" }""");

        var vm = new ReposViewModel(services) { NewPath = repo.Path };
        await vm.ConfirmAddCommand.ExecuteAsync(null);

        // Nothing was scaffolded, so there is nothing to account for.
        Assert.False(vm.HasScaffoldNotes);
    }

}
