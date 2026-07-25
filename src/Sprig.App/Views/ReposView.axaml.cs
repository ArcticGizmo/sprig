using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class ReposView : UserControl
{
    public ReposView()
    {
        InitializeComponent();
        // Feed the path field's auto-complete from the view model's directory suggestions.
        RepoPathBox.AsyncPopulator = PopulatePathsAsync;
    }

    Task<IEnumerable<object>> PopulatePathsAsync(string? text, CancellationToken ct)
    {
        var items = DataContext is ReposViewModel vm
            ? vm.SuggestPaths(text ?? "").Cast<object>()
            : [];
        return Task.FromResult(items);
    }

    // The env-file and compose-file boxes are created inside data templates, so we can't wire their
    // populators by name from the constructor. Attach as each loads; suggestions come from the active
    // editor (repo-rooted), read lazily so they survive the editor being swapped out.
    void RepoPathBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        box.AsyncPopulator = (text, ct) =>
        {
            var editor = (DataContext as ReposViewModel)?.Editor;
            IEnumerable<object> items = editor is null ? [] : editor.SuggestRepoPaths(text ?? "").Cast<object>();
            return Task.FromResult(items);
        };
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
