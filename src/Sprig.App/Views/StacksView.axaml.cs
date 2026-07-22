using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class StacksView : UserControl
{
    public StacksView() => InitializeComponent();

    // A stack is a filter-worthy .json blob; keep the picker scoped to it (with an "all files" escape hatch).
    static readonly IReadOnlyList<FilePickerFileType> StackFileTypes =
    [
        new FilePickerFileType("Sprig stack") { Patterns = ["*.json"] },
        new FilePickerFileType("All files") { Patterns = ["*"] },
    ];

    // Pickers need the window's TopLevel, so the path is chosen here and handed to the view model.
    async void ExportStack(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StacksViewModel vm || vm.Selected is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export stack",
            SuggestedFileName = vm.Selected.Name + ".json",
            DefaultExtension = "json",
            FileTypeChoices = StackFileTypes,
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path) vm.ExportTo(path);
    }

    async void ImportStack(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StacksViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import stack",
            AllowMultiple = false,
            FileTypeFilter = StackFileTypes,
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path) vm.ImportFrom(path);
    }
}
