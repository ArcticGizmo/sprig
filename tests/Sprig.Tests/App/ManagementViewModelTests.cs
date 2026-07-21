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
    public void Compose_override_requires_the_target_file_to_exist()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, ".sprig.json");
        File.WriteAllText(configPath, """{ "schema":1, "name":"api" }""");
        var before = File.ReadAllText(configPath);
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"),
            "services:\n  db:\n    image: postgres:16\n");

        var editor = RepoEditViewModel.Load(dir);
        editor.HasCompose = true;

        // a path with no matching file is flagged and blocks the save
        editor.ComposeFile = "nope.yml";
        Assert.True(editor.ShowComposeMissing);
        Assert.False(editor.Save());
        Assert.Contains("not found", editor.Error);
        Assert.Equal(before, File.ReadAllText(configPath)); // untouched

        // pointing at the real file clears the block and saves
        editor.ComposeFile = "docker-compose.yml";
        Assert.True(editor.ShowComposeFound);
        Assert.True(editor.Save());
    }

    [Fact]
    public void Repo_path_suggestions_are_repo_relative_and_include_files()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "");
        File.WriteAllText(Path.Combine(dir, ".env.example"), "");
        File.WriteAllText(Path.Combine(dir, "README.md"), "");
        Directory.CreateDirectory(Path.Combine(dir, "backend"));
        File.WriteAllText(Path.Combine(dir, "backend", ".env"), "");

        var editor = RepoEditViewModel.Load(dir);

        // prefix match, repo-relative, files included
        var envHits = editor.SuggestRepoPaths(".env");
        Assert.Contains(".env.local", envHits);
        Assert.Contains(".env.example", envHits);
        Assert.DoesNotContain("README.md", envHits);

        // empty input lists the repo root, directories carry a trailing slash
        Assert.Contains("backend/", editor.SuggestRepoPaths(""));

        // drilling into a subdirectory returns nested repo-relative paths
        Assert.Contains("backend/.env", editor.SuggestRepoPaths("backend/"));
    }

    [Fact]
    public async Task Env_override_of_a_git_tracked_file_is_blocked()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, ".sprig.json");
        File.WriteAllText(configPath, """{ "schema":1, "name":"api" }""");
        var before = File.ReadAllText(configPath);

        var git = new FakeGitService();
        git.TrackedFiles.Add(".env");       // committed → off-limits
        git.IgnoredFiles.Add(".env.local"); // gitignored → safe
        var editor = RepoEditViewModel.Load(dir, git);

        editor.AddEnvFileCommand.Execute(null);
        var row = editor.Env.First();
        row.Set.First().Key = "PORT";
        row.Set.First().Value = "5000";

        row.File = ".env";
        await row.StatusReady;
        Assert.Equal(EnvFileStatus.Tracked, row.Status);
        Assert.True(row.ShowTrackedWarning);

        Assert.False(editor.Save());                        // save refused
        Assert.Contains("tracked", editor.Error);
        Assert.Equal(before, File.ReadAllText(configPath)); // file untouched

        // pointing at a gitignored file clears the block and saves
        row.File = ".env.local";
        await row.StatusReady;
        Assert.Equal(EnvFileStatus.Ignored, row.Status);
        Assert.True(row.ShowIgnoredOk);
        Assert.True(editor.Save());
    }

    [Fact]
    public async Task Env_row_suggests_keys_from_the_file_and_its_template()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "PORT=3000\n");
        File.WriteAllText(Path.Combine(dir, ".env.template"), "PORT=\nDATABASE_URL=\n");

        var editor = RepoEditViewModel.Load(dir);
        editor.AddEnvFileCommand.Execute(null);
        var row = editor.Env.First();

        row.File = ".env.local";
        await row.StatusReady;

        Assert.Contains("PORT", row.AvailableKeys);
        Assert.Contains("DATABASE_URL", row.AvailableKeys); // from the committed template
    }

    [Fact]
    public async Task Env_file_that_is_not_gitignored_is_flagged_even_when_missing()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.present"), ""); // exists on disk, but not ignored

        var git = new FakeGitService();
        git.IgnoredFiles.Add(".env.local");
        var editor = RepoEditViewModel.Load(dir, git);
        editor.AddEnvFileCommand.Execute(null);
        var row = editor.Env.First();

        // gitignored → safe
        row.File = ".env.local";
        await row.StatusReady;
        Assert.Equal(EnvFileStatus.Ignored, row.Status);

        // exists but not ignored → amber warning (would surface as a worktree change)
        row.File = ".env.present";
        await row.StatusReady;
        Assert.Equal(EnvFileStatus.NotIgnored, row.Status);
        Assert.True(row.ShowNotIgnoredWarning);

        // no matching file AND not ignored → still warned (the case naive existence detection missed)
        row.File = ".env.ghost";
        await row.StatusReady;
        Assert.Equal(EnvFileStatus.NotIgnoredNew, row.Status);
        Assert.True(row.ShowNotIgnoredWarning);
        Assert.Contains("No matching file", row.NotIgnoredMessage);
    }

    [Fact]
    public void Stacks_create_from_checked_repos_and_remove()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepo(s.Root, "vue"));
        services.Repos.Add(MakeRepo(s.Root, "api"));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
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
        var vm = new StacksViewModel(services, new Navigator()) { NewName = "empty" };

        vm.CreateCommand.Execute(null); // no repos checked

        Assert.NotNull(vm.Error);
        Assert.Empty(vm.Stacks);
    }

    [Fact]
    public void Selected_stack_is_editable_when_no_workspaces_use_it()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepo(s.Root, "vue"));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "solo" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.CreateCommand.Execute(null);
        vm.Selected = vm.Stacks.Single();

        Assert.Equal(0, vm.AttachedWorkspaces);
        Assert.True(vm.CanEditSelected);
        Assert.False(vm.EditBlocked);
    }

    [Fact]
    public void Editing_a_stack_prefills_the_builder_and_rename_drops_the_old()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepo(s.Root, "vue"));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "solo" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.CreateCommand.Execute(null);
        vm.Selected = vm.Stacks.Single();

        vm.EditSelectedCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("solo", vm.NewName);
        Assert.Equal("Edit stack", vm.OverlayTitle);

        vm.NewName = "solo2";
        vm.CreateCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Contains(vm.Stacks, st => st.Name == "solo2");
        Assert.DoesNotContain(vm.Stacks, st => st.Name == "solo");
    }

    [Theory]
    [InlineData("web+api", false)]     // '+' is allowed (filename, not a branch)
    [InlineData("web-api.v2", false)]
    [InlineData("", false)]            // empty: don't nag before typing
    [InlineData("web api", true)]      // space
    [InlineData("web/api", true)]      // path separator
    [InlineData("café", true)]         // non-ASCII
    public void Stack_name_validation_flags_bad_characters(string name, bool expectError)
    {
        using var s = new TempStore();
        var vm = new StacksViewModel(new AppServices(s.Root), new Navigator()) { NewName = name };
        Assert.Equal(expectError, vm.HasNameError);
    }
}
