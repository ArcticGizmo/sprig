using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class ReposView : UserControl
{
    ReposViewModel? _hooked;

    public ReposView()
    {
        InitializeComponent();
        // Feed the path field's auto-complete from the view model's directory suggestions.
        RepoPathBox.AsyncPopulator = PopulatePathsAsync;
        DataContextChanged += OnDataContextChanged;
    }

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hooked is not null) _hooked.OperationStarted -= ShowProgress;
        _hooked = DataContext as ReposViewModel;
        if (_hooked is not null) _hooked.OperationStarted += ShowProgress;
    }

    /// <summary>Open the isolate checklist in its own non-blocking window.</summary>
    void ShowProgress(OperationProgressViewModel vm)
    {
        var window = new OperationProgressWindow { DataContext = vm };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    Task<IEnumerable<object>> PopulatePathsAsync(string? text, CancellationToken ct)
    {
        var items = DataContext is ReposViewModel vm
            ? vm.SuggestPaths(text ?? "").Cast<object>()
            : [];
        return Task.FromResult(items);
    }

    // The env-file, compose-file and module-path boxes are created inside data templates, so we can't
    // wire their populators by name from the constructor. Attach as each loads; suggestions come from the
    // active editor, read lazily so they survive the editor being swapped out. A module's env/compose
    // files are relative to that module's path, so those boxes suggest from within it; the module-path
    // picker itself (DataContext is the module tab) suggests from the repo root.
    void RepoPathBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        box.AsyncPopulator = (text, ct) =>
        {
            var editor = (DataContext as ReposViewModel)?.Editor;
            if (editor is null) return Task.FromResult<IEnumerable<object>>([]);
            var basePath = box.DataContext is ModuleEditTab ? "" : editor.SelectedModule?.Path ?? "";
            return Task.FromResult(editor.SuggestRepoPaths(text ?? "", basePath).Cast<object>());
        };
    }

    // The multi-module add flow's path boxes are created inside a data template, so (like the editor's
    // path boxes) their populator is wired as each loads. Suggestions are directories under the folder
    // being added (the repo isn't registered yet, so this reads the view model's NewPath, not an editor).
    void ModuleSpecPathBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        box.AsyncPopulator = (text, ct) =>
        {
            var vm = DataContext as ReposViewModel;
            if (vm is null) return Task.FromResult<IEnumerable<object>>([]);
            return Task.FromResult(vm.SuggestRepoSubdirs(text ?? "").Cast<object>());
        };
    }

    // Focusing (clicking into) a module-path box shows its directory suggestions straight away. The
    // AutoCompleteBox only searches on a text change, so opening the drop-down is what kicks the async
    // populator — and with MinimumPrefixLength=0 an empty box still lists the repo root's directories.
    void ModuleSpecPathBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    // The module name box lives in a data template, so it can't be focused by name. "+ Add module" leaves
    // the name blank and sets a one-shot flag; when the new tab's editor loads we consume the flag and put
    // the cursor in the name box. Loaded also fires when switching between existing tabs — the flag gates
    // that so we only steal focus for a module the user just added.
    void ModuleNameBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var editor = (DataContext as ReposViewModel)?.Editor;
        if (editor is null || !editor.FocusNewModuleRequested) return;
        editor.FocusNewModuleRequested = false;
        Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
    }

    async void BrowseRepoFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReposViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the repo folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
            vm.NewPath = path;
    }
}
