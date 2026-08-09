using System.Collections.Generic;
using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Stacks;

namespace Sprig.Tests.App;

/// <summary>
/// The Stacks page's right-click "Clone" — make a variation of a complex stack without re-wiring it.
/// These pin the behaviour: the copy carries the full definition under a new, unused name; the prompt
/// suggests a name nothing else uses; and a name that collides (case-insensitively) can't be committed,
/// so a clone never overwrites an existing stack.
/// </summary>
public class StackCloneViewModelTests
{
    /// <summary>api owns api_port; web consumes it and owns web_port. Same shape as the other VM tests.</summary>
    static void SeedWebApi(AppServices services, string root)
    {
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(root, "web",
            ("apiUrl", "http://localhost:4000"), ("devPort", "5173")));
        services.Stacks.Save(new StackDefinition
        {
            Name = "web+api",
            Repos = ["api", "web"],
            Ports = ["api_port", "web_port"],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
                ["web"] = new Dictionary<string, string>
                {
                    ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",
                    ["devPort"] = "${sprig.ports.web_port}",
                },
            },
        });
    }

    static StackDefinition Source(StacksViewModel vm) => vm.Stacks.Single(x => x.Name == "web+api");

    [Fact]
    public void Cloning_copies_the_full_definition_under_the_new_name_and_selects_the_copy()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApi(services, s.Root);

        var vm = new StacksViewModel(services, new Navigator());
        var source = Source(vm);

        vm.StartCloneCommand.Execute(source);
        Assert.True(vm.CloningStack);
        Assert.Equal("web+api-copy", vm.CloneName);   // pre-filled, unique
        Assert.Same(source, vm.Selected);

        vm.CloneName = "web+api-v2";
        Assert.True(vm.ConfirmCloneCommand.CanExecute(null));
        vm.ConfirmCloneCommand.Execute(null);

        // The prompt closes, the original survives, and the copy is now selected.
        Assert.False(vm.CloningStack);
        Assert.Contains(vm.Stacks, x => x.Name == "web+api");
        var copy = vm.Stacks.Single(x => x.Name == "web+api-v2");
        Assert.Same(copy, vm.Selected);

        // The copy carries the source's repos, ports and wiring — persisted, not just in the list.
        Assert.Equal(source.Repos, copy.Repos);
        Assert.Equal(source.Ports, copy.Ports);
        var onDisk = services.Stacks.Get("web+api-v2");
        Assert.NotNull(onDisk);
        Assert.Equal(source.Bindings["web"]["apiUrl"], onDisk!.Bindings["web"]["apiUrl"]);
    }

    [Fact]
    public void The_suggested_clone_name_steps_past_names_already_taken()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApi(services, s.Root);
        services.Stacks.Save(new StackDefinition
        {
            Name = "web+api-copy",
            Repos = ["api"],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        });

        var vm = new StacksViewModel(services, new Navigator());
        vm.StartCloneCommand.Execute(Source(vm));

        Assert.Equal("web+api-copy-2", vm.CloneName);
    }

    [Fact]
    public void Cloning_to_a_name_already_in_use_is_blocked()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApi(services, s.Root);
        services.Stacks.Save(new StackDefinition
        {
            Name = "taken",
            Repos = ["api"],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        });

        var vm = new StacksViewModel(services, new Navigator());
        vm.StartCloneCommand.Execute(Source(vm));

        vm.CloneName = "taken";
        Assert.True(vm.HasCloneNameError);
        Assert.False(vm.ConfirmCloneCommand.CanExecute(null));

        // Names are filenames — case-insensitive on Windows — so a case variant collides too.
        vm.CloneName = "TAKEN";
        Assert.False(vm.ConfirmCloneCommand.CanExecute(null));

        // A distinct, valid name clears the block.
        vm.CloneName = "fresh";
        Assert.False(vm.HasCloneNameError);
        Assert.True(vm.ConfirmCloneCommand.CanExecute(null));
    }

    [Fact]
    public void A_clone_name_with_disallowed_characters_is_rejected()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApi(services, s.Root);

        var vm = new StacksViewModel(services, new Navigator());
        vm.StartCloneCommand.Execute(Source(vm));

        vm.CloneName = "bad name";   // space is not allowed
        Assert.True(vm.HasCloneNameError);
        Assert.False(vm.ConfirmCloneCommand.CanExecute(null));
    }

    [Fact]
    public void Selecting_another_stack_dismisses_an_open_clone_prompt()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApi(services, s.Root);
        services.Stacks.Save(new StackDefinition
        {
            Name = "solo",
            Repos = ["api"],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        });

        var vm = new StacksViewModel(services, new Navigator());
        vm.StartCloneCommand.Execute(Source(vm));
        Assert.True(vm.CloningStack);

        vm.Selected = vm.Stacks.Single(x => x.Name == "solo");
        Assert.False(vm.CloningStack);
    }
}
