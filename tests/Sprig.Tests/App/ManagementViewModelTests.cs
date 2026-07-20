using Sprig.App;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// VM tests over a temp central store. These exercise the VM→Core wiring for the synchronous
/// management flows (repos + stacks). Workspace lifecycle VMs are covered by the headless-render
/// integration + the Core tests they delegate to.
/// </summary>
public class ManagementViewModelTests
{
    static string MakeRepo(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":1, "name":"{{name}}" }""");
        return dir;
    }

    [Fact]
    public async Task Repos_register_and_unregister()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var repoDir = MakeRepo(s.Root, "vue");

        var vm = new ReposViewModel(services) { NewPath = repoDir };
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Contains(vm.Repos, r => r.Name == "vue");
        Assert.Equal("", vm.NewPath); // cleared on success

        vm.Selected = vm.Repos.First(r => r.Name == "vue");
        vm.RemoveCommand.Execute(null);
        Assert.Empty(vm.Repos);
    }

    [Fact]
    public async Task Repos_modal_detects_existing_config_and_registers()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var repoDir = MakeRepo(s.Root, "vue");

        var vm = new ReposViewModel(services);
        vm.OpenAddCommand.Execute(null);
        Assert.True(vm.IsAdding);

        vm.NewPath = repoDir;
        Assert.True(vm.PathHasConfig);
        Assert.Equal("Register", vm.AddButtonLabel);

        await vm.ConfirmAddCommand.ExecuteAsync(null);

        Assert.False(vm.IsAdding); // modal closes on success
        Assert.Contains(vm.Repos, r => r.Name == "vue");
    }

    [Fact]
    public async Task Repos_modal_inits_a_config_when_none_present()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var bare = Path.Combine(s.Root, "bare");
        Directory.CreateDirectory(bare);

        var vm = new ReposViewModel(services);
        vm.OpenAddCommand.Execute(null);
        vm.NewPath = bare;

        Assert.False(vm.PathHasConfig);
        Assert.Equal("Initialize & register", vm.AddButtonLabel);

        await vm.ConfirmAddCommand.ExecuteAsync(null);

        Assert.Null(vm.Error);
        Assert.False(vm.IsAdding);
        Assert.True(File.Exists(Path.Combine(bare, ".sprig.json"))); // init wrote it
        Assert.Contains(vm.Repos, r => r.Name == "bare");
    }

    [Fact]
    public async Task Repos_add_surfaces_error_for_non_sprig_path()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var plain = Path.Combine(s.Root, "plain");
        Directory.CreateDirectory(plain);

        var vm = new ReposViewModel(services) { NewPath = plain };
        await vm.AddCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Error);
        Assert.Empty(vm.Repos);
    }

    [Fact]
    public async Task Repos_edit_changes_a_value_and_saves_it_back()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """
            { "schema":1, "name":"api",
              "inputs":[ { "name":"port", "example":"5000" } ],
              "env":[ { "file":".env", "set":{ "PORT":"${sprig.port}" } } ] }
            """);

        var vm = new ReposViewModel(services) { NewPath = dir };
        await vm.AddCommand.ExecuteAsync(null);
        vm.Selected = vm.Repos.First(r => r.Name == "api");

        vm.BeginEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("api", vm.Editor!.Name);

        // change the input's example and add a new env key
        vm.Editor.Inputs.First().Example = "6000";
        var env = vm.Editor.Env.First();
        env.AddKeyCommand.Execute(null);
        env.Set.Last().Key = "HOST";
        env.Set.Last().Value = "localhost";

        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsEditing);            // form closes on success
        Assert.Contains("saved", vm.Status);

        // re-read from disk to prove it persisted
        var reloaded = RepoEditViewModel.Load(dir);
        Assert.Equal("6000", reloaded.Inputs.First().Example);
        Assert.Contains(reloaded.Env.First().Set, k => k.Key == "HOST" && k.Value == "localhost");
    }

    [Fact]
    public void Repos_edit_with_invalid_input_name_surfaces_error_and_does_not_write()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, ".sprig.json");
        File.WriteAllText(configPath, """{ "schema":1, "name":"api", "inputs":[ { "name":"port" } ] }""");
        var before = File.ReadAllText(configPath);

        services.Repos.Add(dir);
        var vm = new ReposViewModel(services);
        vm.Selected = vm.Repos.First(r => r.Name == "api");
        vm.BeginEditCommand.Execute(null);

        vm.Editor!.Inputs.First().Name = "bad name!"; // spaces/'!' are not identifier chars
        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsEditing);              // stays in edit mode
        Assert.NotNull(vm.Editor.Error);
        Assert.Equal(before, File.ReadAllText(configPath)); // file untouched
    }

    [Fact]
    public void Repos_add_flags_missing_git_repo()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = new ReposViewModel(services);

        // empty path: no highlight either way
        Assert.False(vm.PathEntered);
        Assert.False(vm.GitOk);
        Assert.False(vm.GitMissing);

        // a plain folder with no .git
        var plain = Path.Combine(s.Root, "plain");
        Directory.CreateDirectory(plain);
        vm.NewPath = plain;
        Assert.True(vm.PathEntered);
        Assert.False(vm.PathIsGitRepo);
        Assert.True(vm.GitMissing);
        Assert.False(vm.GitOk);

        // add a .git dir → now it reads as a git repo
        Directory.CreateDirectory(Path.Combine(plain, ".git"));
        vm.NewPath = plain + " "; // change value to retrigger detection
        Assert.True(vm.PathIsGitRepo);
        Assert.True(vm.GitOk);
        Assert.False(vm.GitMissing);
    }

    [Fact]
    public void Repos_path_suggestions_match_the_typed_prefix()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = new ReposViewModel(services);

        var root = Path.Combine(s.Root, "code");
        Directory.CreateDirectory(Path.Combine(root, "proj-a"));
        Directory.CreateDirectory(Path.Combine(root, "proj-b"));
        Directory.CreateDirectory(Path.Combine(root, "other"));

        var hits = vm.SuggestPaths(Path.Combine(root, "proj"));

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.EndsWith("proj-a"));
        Assert.Contains(hits, h => h.EndsWith("proj-b"));
        Assert.DoesNotContain(hits, h => h.EndsWith("other"));
        Assert.Empty(vm.SuggestPaths("")); // nothing typed → no suggestions
    }

    [Fact]
    public void Stacks_create_from_checked_repos_and_remove()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepo(s.Root, "vue"));
        services.Repos.Add(MakeRepo(s.Root, "api"));

        var vm = new StacksViewModel(services) { NewName = "web+api" };
        Assert.Equal(2, vm.RepoChoices.Count);
        foreach (var c in vm.RepoChoices) c.IsSelected = true;

        vm.CreateCommand.Execute(null);

        Assert.Contains(vm.Stacks, st => st.Name == "web+api" && st.Repos.Count == 2);

        vm.Selected = vm.Stacks.First(st => st.Name == "web+api");
        vm.RemoveCommand.Execute(null);
        Assert.DoesNotContain(vm.Stacks, st => st.Name == "web+api");
    }

    [Fact]
    public void Stacks_create_with_no_repos_surfaces_error()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = new StacksViewModel(services) { NewName = "empty" };

        vm.CreateCommand.Execute(null); // no repos checked

        Assert.NotNull(vm.Error);
        Assert.Empty(vm.Stacks);
    }
}
