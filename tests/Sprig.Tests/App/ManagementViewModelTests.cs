using Sprig.App;
using Sprig.App.Controls;
using Sprig.App.ViewModels;
using Sprig.Core.Stacks;

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
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":2, "name":"{{name}}" }""");
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
        Assert.Equal("Load & edit", vm.AddButtonLabel);

        await vm.ConfirmAddCommand.ExecuteAsync(null);

        Assert.False(vm.IsAdding); // modal closes on success
        Assert.Contains(vm.Repos, r => r.Name == "vue");
        // Lands straight in the editor for the repo just added.
        Assert.True(vm.IsEditing);
        Assert.Equal("vue", vm.Editor!.Name);
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
        Assert.Equal("Create & edit", vm.AddButtonLabel);

        await vm.ConfirmAddCommand.ExecuteAsync(null);

        Assert.Null(vm.Error);
        Assert.False(vm.IsAdding);
        Assert.True(File.Exists(Path.Combine(bare, ".sprig.json"))); // init wrote it
        Assert.Contains(vm.Repos, r => r.Name == "bare");
        // The scaffolded config opens for editing straight away.
        Assert.True(vm.IsEditing);
        Assert.Equal("bare", vm.Editor!.Name);
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
            { "schema":2, "name":"api",
              "inputs":[ { "name":"port", "example":"5000" } ],
              "env":[ { "file":".env", "set":{ "PORT":"${sprig.port}" } } ] }
            """);

        var vm = new ReposViewModel(services) { NewPath = dir };
        await vm.AddCommand.ExecuteAsync(null);
        vm.Selected = vm.Repos.First(r => r.Name == "api");

        vm.BeginEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("api", vm.Editor!.Name);

        // change the input's example and add a new env override via the overlay
        vm.Editor.Inputs.First().Example = "6000";
        var env = vm.Editor.Env.First();
        await env.StatusReady;                     // let the merged-env overlay build
        var overlay = env.Overlay!;
        overlay.NewKey = "HOST";
        overlay.AddKeyCommand.Execute(null);
        var host = overlay.Keys.Single(k => k.Key == "HOST");
        host.Draft = "localhost";
        overlay.ApplyCommand.Execute(host);

        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsEditing);            // form closes on success
        Assert.Contains("saved", vm.Status);

        // re-read from disk to prove it persisted
        var reloaded = RepoEditViewModel.Load(dir);
        Assert.Equal("6000", reloaded.Inputs.First().Example);
        Assert.Contains(reloaded.Env.First().CurrentSet, k => k.Key == "HOST" && k.Value == "localhost");
    }

    [Fact]
    public void Repos_edit_with_invalid_input_name_surfaces_error_and_does_not_write()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, ".sprig.json");
        File.WriteAllText(configPath, """{ "schema":2, "name":"api", "inputs":[ { "name":"port" } ] }""");
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
        File.WriteAllText(configPath, """{ "schema":2, "name":"api" }""");
        var before = File.ReadAllText(configPath);
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"),
            "services:\n  db:\n    image: postgres:16\n");

        var editor = RepoEditViewModel.Load(dir);
        editor.AddComposeFileCommand.Execute(null);
        var row = editor.Compose.First();

        // a path with no matching file is flagged and blocks the save
        row.File = "nope.yml";
        Assert.True(row.ShowMissing);
        Assert.False(editor.Save());
        Assert.Contains("not found", editor.Error);
        Assert.Equal(before, File.ReadAllText(configPath)); // untouched

        // pointing at the real file clears the block and saves
        row.File = "docker-compose.yml";
        Assert.True(row.ShowFound);
        Assert.True(editor.Save());
    }

    [Fact]
    public void Compose_files_round_trip_through_load_and_save()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":2, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services:\n  a:\n    image: x\n");
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        File.WriteAllText(Path.Combine(dir, "web", "compose.yaml"), "services:\n  b:\n    image: y\n");

        var editor = RepoEditViewModel.Load(dir);
        editor.AddComposeFileCommand.Execute(null);
        editor.Compose[0].File = "docker-compose.yml";
        editor.AddComposeFileCommand.Execute(null);
        editor.Compose[1].File = "web/compose.yaml";
        Assert.True(editor.Save());

        var reloaded = RepoEditViewModel.Load(dir);
        Assert.Equal(["docker-compose.yml", "web/compose.yaml"], reloaded.Compose.Select(c => c.File));
    }

    [Fact]
    public void Repo_path_suggestions_are_repo_relative_and_include_files()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":2, "name":"api" }""");
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
        File.WriteAllText(configPath, """{ "schema":2, "name":"api" }""");
        var before = File.ReadAllText(configPath);

        var git = new FakeGitService();
        git.TrackedFiles.Add(".env");       // committed → off-limits
        git.IgnoredFiles.Add(".env.local"); // gitignored → safe
        var editor = RepoEditViewModel.Load(dir, git);

        editor.AddEnvFileCommand.Execute(null);
        var row = editor.Env.First();

        row.File = ".env";
        await row.StatusReady;
        Assert.Equal(EnvFileStatus.Tracked, row.Status);
        Assert.True(row.ShowTrackedWarning);

        // set an override key via the overlay (the merged-env editor)
        var overlay = row.Overlay!;
        overlay.NewKey = "PORT";
        overlay.AddKeyCommand.Execute(null);
        var port = overlay.Keys.Single(k => k.Key == "PORT");
        port.Draft = "5000";
        overlay.ApplyCommand.Execute(port);

        Assert.False(editor.Save());                        // save refused (tracked file)
        Assert.Contains("tracked", editor.Error);
        Assert.Equal(before, File.ReadAllText(configPath)); // file untouched

        // pointing at a gitignored file clears the block — the override carries across the file change
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
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":2, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "PORT=3000\n");
        File.WriteAllText(Path.Combine(dir, ".env.template"), "PORT=\nDATABASE_URL=\n");

        var editor = RepoEditViewModel.Load(dir);
        editor.AddEnvFileCommand.Execute(null);
        var row = editor.Env.First();

        row.File = ".env.local";
        await row.StatusReady;

        Assert.Contains(row.Overlay!.Keys, k => k.Key == "PORT");
        Assert.Contains(row.Overlay!.Keys, k => k.Key == "DATABASE_URL"); // from the committed template
    }

    [Fact]
    public async Task Env_row_suggests_keys_from_an_explicitly_added_template()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":2, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "PORT=3000\n");
        // A non-conventional name the companion heuristics would never find — only an explicit template does.
        File.WriteAllText(Path.Combine(dir, "shared.env"), "SHARED_SECRET=\nAPI_KEY=\n");

        var editor = RepoEditViewModel.Load(dir);
        editor.AddEnvFileCommand.Execute(null);
        var row = editor.Env.First();
        row.File = ".env.local";
        await row.StatusReady;

        row.AddTemplateCommand.Execute(null);
        row.Templates.First().Path = "shared.env";
        await row.StatusReady;

        Assert.Contains(row.Overlay!.Keys, k => k.Key == "PORT");          // still the target file's own key
        Assert.Contains(row.Overlay!.Keys, k => k.Key == "SHARED_SECRET"); // pulled in from the added template
        Assert.Contains(row.Overlay!.Keys, k => k.Key == "API_KEY");
    }

    [Fact]
    public async Task Referenced_but_undeclared_input_is_offered_as_quick_add_and_blocks_save()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """
            { "schema":2, "name":"api",
              "env":[ { "file":".env.local", "set":{ "PORT":"${sprig.port}" } } ] }
            """);

        var editor = RepoEditViewModel.Load(dir);
        await editor.Env.First().StatusReady;   // let the env overlay settle

        // "port" is referenced by the override but not declared → surfaced for quick add,
        // and that same gap blocks the save.
        Assert.Contains("port", editor.MissingInputRefs);
        Assert.True(editor.HasMissingInputRefs);
        Assert.False(editor.Save());
        Assert.Contains("port", editor.Error);

        // quick-add declares it: the chip clears and the config now saves
        editor.QuickAddInputCommand.Execute("port");
        Assert.DoesNotContain("port", editor.MissingInputRefs);
        Assert.False(editor.HasMissingInputRefs);
        Assert.Contains(editor.Inputs, i => i.Name == "port");
        Assert.True(editor.Save());
    }

    [Fact]
    public async Task Env_file_that_is_not_gitignored_is_flagged_even_when_missing()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":2, "name":"api" }""");
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
        vm.RemoveCommand.Execute(null);          // opens the confirm bar
        Assert.True(vm.ConfirmingRemove);
        vm.ConfirmRemoveCommand.Execute(null);   // actually removes
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

    // A repo whose .sprig.json declares inputs, for exercising the builder's wiring aids.
    static string MakeRepoWithInputs(string root, string name, params (string Name, string Example)[] inputs)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var decls = string.Join(",", inputs.Select(i => $$"""{ "name":"{{i.Name}}", "example":"{{i.Example}}" }"""));
        File.WriteAllText(Path.Combine(dir, ".sprig.json"),
            $$"""{ "schema":2, "name":"{{name}}", "inputs":[ {{decls}} ] }""");
        return dir;
    }

    static BindingRow Row(StacksViewModel vm, string repo, string input) =>
        vm.Bindings.First(g => g.Repo == repo).Rows.First(r => r.Input == input);

    [Fact]
    public void AutoWire_fills_unbound_bindings_and_adds_ports()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "vue", ("frontend", "3000"), ("apiUrl", "http://localhost:4000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.AutoWireCommand.Execute(null);

        Assert.Equal("${sprig.ports.frontend_port}", Row(vm, "vue", "frontend").Expression);
        Assert.Equal("http://localhost:${sprig.ports.api_port}", Row(vm, "vue", "apiUrl").Expression);
        Assert.Contains(vm.Ports, p => p.Name == "frontend_port");
        Assert.Contains(vm.Ports, p => p.Name == "api_port");
    }

    static StacksViewModel WebPlusApi(AppServices services, string root)
    {
        services.Repos.Add(MakeRepoWithInputs(root, "vue", ("apiUrl", "http://localhost:4000")));
        services.Repos.Add(MakeRepoWithInputs(root, "api", ("port", "5000")));
        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
        foreach (var c in vm.RepoChoices) c.IsSelected = true;
        vm.AddPortCommand.Execute(null);
        vm.Ports.Single().Name = "api_port";
        return vm;
    }

    [Fact]
    public void Saving_persists_the_shared_port_relationship()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = WebPlusApi(services, s.Root);

        Row(vm, "vue", "apiUrl").Expression = "http://localhost:${sprig.ports.api_port}";
        Row(vm, "api", "port").Expression = "${sprig.ports.api_port}";
        vm.CreateCommand.Execute(null);

        var saved = services.Stacks.Get("web+api");
        Assert.NotNull(saved);
        var share = Assert.Single(saved!.Shares);
        Assert.Equal("api_port", share.Port);
        Assert.Equal(2, share.Consumers.Count);
    }

    [Fact]
    public void Renaming_a_port_rewrites_every_binding_that_uses_it()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "vue", ("frontend", "3000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.AutoWireCommand.Execute(null);
        Assert.Equal("${sprig.ports.frontend_port}", Row(vm, "vue", "frontend").Expression);

        vm.Ports.Single().Name = "web_port"; // commit a rename

        Assert.Equal("${sprig.ports.web_port}", Row(vm, "vue", "frontend").Expression);
    }

    [Fact]
    public void Selecting_a_stack_populates_the_detail_summary()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = WebPlusApi(services, s.Root);
        Row(vm, "vue", "apiUrl").Expression = "http://localhost:${sprig.ports.api_port}";
        Row(vm, "api", "port").Expression = "${sprig.ports.api_port}";
        vm.CreateCommand.Execute(null);

        vm.Selected = vm.Stacks.Single();

        // The read-only detail pane lists each repo's inputs and their expressions.
        var vue = vm.DetailBindings.Single(b => b.Repo == "vue");
        Assert.Contains(vue.Rows, r => r.Input == "apiUrl" && r.Expression == "http://localhost:${sprig.ports.api_port}");
        var api = vm.DetailBindings.Single(b => b.Repo == "api");
        Assert.Contains(api.Rows, r => r.Input == "port" && r.Expression == "${sprig.ports.api_port}");
    }

    [Fact]
    public void WirePin_binds_the_input_to_the_port_and_updates_the_live_graph()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = WebPlusApi(services, s.Root);

        vm.WirePinCommand.Execute(new WireRequest("vue", "apiUrl", "api_port"));

        Assert.Equal("${sprig.ports.api_port}", Row(vm, "vue", "apiUrl").Expression);
        Assert.NotNull(vm.BuilderWiring);
        Assert.Contains(vm.BuilderWiring!.Edges, e => e is { Repo: "vue", Input: "apiUrl", Port: "api_port" });
    }

    [Fact]
    public void UnwirePin_clears_the_binding()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = WebPlusApi(services, s.Root);
        vm.WirePinCommand.Execute(new WireRequest("api", "port", "api_port"));
        Assert.NotEqual("", Row(vm, "api", "port").Expression);

        vm.UnwirePinCommand.Execute(new PinRef("api", "port"));

        Assert.Equal("", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void Wiring_two_pins_to_one_port_marks_it_shared_in_the_live_graph()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var vm = WebPlusApi(services, s.Root);

        vm.WirePinCommand.Execute(new WireRequest("vue", "apiUrl", "api_port"));
        vm.WirePinCommand.Execute(new WireRequest("api", "port", "api_port"));

        Assert.Contains(vm.BuilderWiring!.Ports, p => p.Name == "api_port" && p.Shared);
    }

    // --- Phase 2: source→input drag commands (create-on-drop, workspace, replace) --------------

    [Fact]
    public void CreatePort_mints_a_new_port_and_wires_the_input_to_it()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.CreatePortCommand.Execute(new CreatePortRequest("api", "port", "api_port"));

        Assert.Contains(vm.Ports, p => p.Name == "api_port");
        Assert.Equal("${sprig.ports.api_port}", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void CreatePort_reuses_an_existing_port_of_the_same_name_rather_than_duplicating()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "vue", ("apiUrl", "http://localhost:4000")));
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web+api" };
        foreach (var c in vm.RepoChoices) c.IsSelected = true;

        vm.CreatePortCommand.Execute(new CreatePortRequest("api", "port", "api_port"));
        vm.CreatePortCommand.Execute(new CreatePortRequest("vue", "apiUrl", "api_port")); // same name

        Assert.Single(vm.Ports.Where(p => p.Name == "api_port")); // not duplicated
        // Both inputs now consume the one port — the live graph marks it shared.
        Assert.Contains(vm.BuilderWiring!.Ports, p => p.Name == "api_port" && p.Shared);
    }

    [Fact]
    public void CreatePort_ignores_a_blank_name()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.CreatePortCommand.Execute(new CreatePortRequest("api", "port", "   "));

        Assert.Empty(vm.Ports);
        Assert.Equal("", Row(vm, "api", "port").Expression);
    }

    [Fact]
    public void WireWorkspace_binds_the_input_to_the_workspace_source()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("name", "svc")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.WireWorkspaceCommand.Execute(new PinRef("api", "name"));

        Assert.Equal("${sprig.workspace}", Row(vm, "api", "name").Expression);
        Assert.Contains(vm.BuilderWiring!.Repos.SelectMany(r => r.Pins), p => p is { Input: "name", UsesWorkspace: true });
        Assert.True(vm.BuilderWiring.Workspace.Used);
    }

    [Fact]
    public void SetExpression_types_a_literal_directly_onto_an_input()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("env", "production")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.SetExpressionCommand.Execute(new SetExpressionRequest("api", "env", "production"));

        Assert.Equal("production", Row(vm, "api", "env").Expression);
        Assert.Contains(vm.BuilderWiring!.Repos.SelectMany(r => r.Pins), p => p is { Input: "env", IsLiteral: true });
        Assert.Empty(vm.BuilderWiring.TransformNodes);
    }

    [Fact]
    public void SetExpression_with_a_wrapped_workspace_creates_a_transform_node()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("name", "svc")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.SetExpressionCommand.Execute(new SetExpressionRequest("api", "name", "svc-${sprig.workspace}"));

        Assert.Contains(vm.BuilderWiring!.TransformNodes, n => n is { Repo: "api", Input: "name", UsesWorkspace: true });
    }

    // --- Phase 5: multi-input transforms (fan-in) -----------------------------------------------

    [Fact]
    public void AppendSource_fans_a_second_port_into_a_transform()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("addr", "a:b")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.AddNamedPortCommand.Execute("host_port");
        vm.AddNamedPortCommand.Execute("admin_port");

        // Start from a transform over one port, then fan a second port into its node.
        vm.SetExpressionCommand.Execute(new SetExpressionRequest("api", "addr", "${sprig.ports.host_port}:"));
        vm.AppendSourceCommand.Execute(new AppendSourceRequest("api", "addr", "${sprig.ports.admin_port}"));

        Assert.Equal("${sprig.ports.host_port}:${sprig.ports.admin_port}", Row(vm, "api", "addr").Expression);

        // The live graph shows one node fed by both ports (two edges, one transform node).
        var node = Assert.Single(vm.BuilderWiring!.TransformNodes, n => n is { Repo: "api", Input: "addr" });
        Assert.Equal(["host_port", "admin_port"], node.Ports);
        Assert.Equal(2, vm.BuilderWiring.Edges.Count(e => e is { Repo: "api", Input: "addr" }));
    }

    [Fact]
    public void AppendSource_ignores_a_source_already_present()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("addr", "a")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.AddNamedPortCommand.Execute("host_port");
        vm.SetExpressionCommand.Execute(new SetExpressionRequest("api", "addr", "${sprig.ports.host_port}"));

        vm.AppendSourceCommand.Execute(new AppendSourceRequest("api", "addr", "${sprig.ports.host_port}"));

        Assert.Equal("${sprig.ports.host_port}", Row(vm, "api", "addr").Expression); // unchanged
    }

    // --- Phase 4: port management from the canvas -----------------------------------------------

    [Fact]
    public void AddNamedPort_adds_a_port_and_ignores_blanks_and_duplicates()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;

        vm.AddNamedPortCommand.Execute("api_port");
        Assert.Contains(vm.Ports, p => p.Name == "api_port");

        vm.AddNamedPortCommand.Execute("api_port"); // duplicate ignored
        vm.AddNamedPortCommand.Execute("   ");        // blank ignored
        Assert.Single(vm.Ports);
    }

    [Fact]
    public void RenamePort_from_the_canvas_rewrites_bindings_and_rejects_collisions()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "vue", ("frontend", "3000")));
        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.AutoWireCommand.Execute(null);
        Assert.Equal("${sprig.ports.frontend_port}", Row(vm, "vue", "frontend").Expression);

        vm.RenamePortCommand.Execute(new RenamePortRequest("frontend_port", "web_port"));
        Assert.Contains(vm.Ports, p => p.Name == "web_port");
        Assert.Equal("${sprig.ports.web_port}", Row(vm, "vue", "frontend").Expression);

        // Renaming onto an existing port name is rejected (no silent merge).
        vm.AddNamedPortCommand.Execute("other");
        vm.RenamePortCommand.Execute(new RenamePortRequest("web_port", "other"));
        Assert.Contains(vm.Ports, p => p.Name == "web_port"); // unchanged
    }

    [Fact]
    public void RemoveNamedPort_from_the_canvas_drops_the_port()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "vue", ("frontend", "3000")));
        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.AutoWireCommand.Execute(null);
        Assert.Contains(vm.Ports, p => p.Name == "frontend_port");

        vm.RemoveNamedPortCommand.Execute("frontend_port");
        Assert.DoesNotContain(vm.Ports, p => p.Name == "frontend_port");
    }

    [Fact]
    public void Dropping_a_port_on_an_already_bound_input_replaces_the_binding()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        services.Repos.Add(MakeRepoWithInputs(s.Root, "api", ("port", "5000")));

        var vm = new StacksViewModel(services, new Navigator()) { NewName = "web" };
        vm.RepoChoices.Single().IsSelected = true;
        vm.CreatePortCommand.Execute(new CreatePortRequest("api", "port", "first_port"));
        Assert.Equal("${sprig.ports.first_port}", Row(vm, "api", "port").Expression);

        // Drop a different port on the same (bound) input → the repo side is single, so it replaces.
        vm.CreatePortCommand.Execute(new CreatePortRequest("api", "port", "second_port"));

        Assert.Equal("${sprig.ports.second_port}", Row(vm, "api", "port").Expression);
    }
}
