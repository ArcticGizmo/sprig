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
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":1, "name":"{{name}}" }""");
        return dir;
    }

    /// <summary>The editor now edits modules as tabs; these single-module tests work against the current
    /// tab — the one a migrated config produced, or a freshly added empty one.</summary>
    static ModuleEditTab Mod(RepoEditViewModel e)
    {
        if (e.Modules.Count == 0)
        {
            e.AddModuleCommand.Execute(null);
            e.SelectedModule!.Name = "app";   // "+ Add module" leaves the name blank for the user to type;
        }                                     // these tests need a named (saveable) module to work against.
        return e.SelectedModule!;
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
    public async Task Repos_modal_scaffolds_multiple_modules_from_definitions()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var repo = Path.Combine(s.Root, "mono");
        Directory.CreateDirectory(Path.Combine(repo, "apps", "web"));
        Directory.CreateDirectory(Path.Combine(repo, "services", "api"));
        File.WriteAllText(Path.Combine(repo, "apps", "web", ".env.local"), "PORT=3000\n");
        File.WriteAllText(Path.Combine(repo, "services", "api", ".env.local"), "PORT=5000\n");

        var vm = new ReposViewModel(services);
        vm.OpenAddCommand.Execute(null);
        vm.NewPath = repo;
        vm.MultiModule = true;   // seeds one blank module row

        vm.ModuleSpecs[0].Name = "web";
        vm.ModuleSpecs[0].Path = "apps/web";
        vm.AddModuleSpecRowCommand.Execute(null);
        vm.ModuleSpecs[1].Name = "api";
        vm.ModuleSpecs[1].Path = "services/api";

        Assert.Equal("Create modules & edit", vm.AddButtonLabel);

        await vm.ConfirmAddCommand.ExecuteAsync(null);

        Assert.Null(vm.Error);
        Assert.False(vm.IsAdding);

        // Both modules were scanned and written, each with its env file stored relative to its path.
        var config = Sprig.Core.Config.SprigConfigLoader.LoadFromFile(Path.Combine(repo, ".sprig.json"));
        Assert.Equal(["web", "api"], config.Modules.Select(m => m.Name));
        Assert.Equal("apps/web", config.Modules[0].Path);
        Assert.Equal(".env.local", config.Modules[0].Env.Single().File);
        Assert.Equal("services/api", config.Modules[1].Path);
        Assert.Equal(".env.local", config.Modules[1].Env.Single().File);
    }

    [Fact]
    public async Task Repos_modal_multi_module_needs_at_least_one_named_module()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var repo = Path.Combine(s.Root, "mono");
        Directory.CreateDirectory(repo);

        var vm = new ReposViewModel(services);
        vm.OpenAddCommand.Execute(null);
        vm.NewPath = repo;
        vm.MultiModule = true;   // seeds one blank (unnamed) row and nothing else

        await vm.ConfirmAddCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Error);                                    // asked to name a module
        Assert.False(File.Exists(Path.Combine(repo, ".sprig.json"))); // nothing scaffolded
        Assert.True(vm.IsAdding);                                    // modal stays open
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
    public async Task Repos_edit_adds_an_env_override_and_saves_it_back()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        // Map-model config: a module that PROVIDES a port capability, its env referencing that provide.
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """
            { "schema":1, "name":"api",
              "modules":[ { "name":"api",
                "provides":[ { "capability":"api", "outputs": { "port": { "port": true } } } ],
                "env":[ { "file":".env", "set":{ "PORT":"${sprig.api.port}" } } ] } ] }
            """);

        var vm = new ReposViewModel(services) { NewPath = dir };
        await vm.AddCommand.ExecuteAsync(null);
        vm.Selected = vm.Repos.First(r => r.Name == "api");

        vm.BeginEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("api", vm.Editor!.Name);

        // add a new env override via the merged-env overlay
        var env = Mod(vm.Editor!).Env.First();
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
        Assert.Contains(reloaded.Modules[0].Env.First().CurrentSet, k => k.Key == "HOST" && k.Value == "localhost");
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
        var mod = Mod(editor);
        mod.AddComposeFileCommand.Execute(null);
        var row = mod.Compose.First();

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
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services:\n  a:\n    image: x\n");
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        File.WriteAllText(Path.Combine(dir, "web", "compose.yaml"), "services:\n  b:\n    image: y\n");

        var editor = RepoEditViewModel.Load(dir);
        var mod = Mod(editor);
        mod.AddComposeFileCommand.Execute(null);
        mod.Compose[0].File = "docker-compose.yml";
        mod.AddComposeFileCommand.Execute(null);
        mod.Compose[1].File = "web/compose.yaml";
        Assert.True(editor.Save());

        var reloaded = RepoEditViewModel.Load(dir);
        Assert.Equal(["docker-compose.yml", "web/compose.yaml"], reloaded.Modules[0].Compose.Select(c => c.File));
    }

    [Fact]
    public void Setup_commands_round_trip_through_load_and_save()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"),
            """{ "schema":1, "name":"api", "setup":["npm ci"] }""");

        var editor = RepoEditViewModel.Load(dir);
        var mod = Mod(editor);
        Assert.Equal(["npm ci"], mod.Setup.Select(x => x.Command));

        mod.AddSetupCommandCommand.Execute(null);
        mod.Setup[1].Command = "dotnet restore";
        mod.AddSetupCommandCommand.Execute(null);   // a blank row is dropped on save
        Assert.True(editor.Save());

        var reloaded = RepoEditViewModel.Load(dir);
        Assert.Equal(["npm ci", "dotnet restore"], reloaded.Modules[0].Setup.Select(x => x.Command));
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

        var mod = Mod(editor);
        mod.AddEnvFileCommand.Execute(null);
        var row = mod.Env.First();

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
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "PORT=3000\n");
        File.WriteAllText(Path.Combine(dir, ".env.template"), "PORT=\nDATABASE_URL=\n");

        var editor = RepoEditViewModel.Load(dir);
        var mod = Mod(editor);
        mod.AddEnvFileCommand.Execute(null);
        var row = mod.Env.First();

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
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"api" }""");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "PORT=3000\n");
        // A non-conventional name the companion heuristics would never find — only an explicit template does.
        File.WriteAllText(Path.Combine(dir, "shared.env"), "SHARED_SECRET=\nAPI_KEY=\n");

        var editor = RepoEditViewModel.Load(dir);
        var mod = Mod(editor);
        mod.AddEnvFileCommand.Execute(null);
        var row = mod.Env.First();
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
        var mod = Mod(editor);
        mod.AddEnvFileCommand.Execute(null);
        var row = mod.Env.First();

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
}
