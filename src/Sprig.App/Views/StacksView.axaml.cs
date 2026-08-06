using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sprig.App.Coach;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class StacksView : UserControl
{
    StacksViewModel? _hooked;
    StackEditorWindow? _editor;
    // The main window's coach layer, dimmed out while the editor window owns the coachmark.
    CoachOverlay? _mainCoach;

    bool _attached;

    public StacksView()
    {
        InitializeComponent();
        // The builder now lives in its own resizable window. We open/close it in response to the view
        // model's IsCreating, so the same NewStack/EditSelected/Create/Cancel commands (and the guided
        // tour, which drives them) keep working — they just flip a flag and the window follows. The VM
        // subscription is bound to the loaded lifetime: a view that's been unloaded (e.g. a closed render
        // window) must not react and try to open a child window against a dead owner.
        Loaded += (_, _) => { _attached = true; Hook(); SyncEditorWindow(); };
        Unloaded += (_, _) =>
        {
            _attached = false;
            if (_hooked is not null) _hooked.PropertyChanged -= OnViewModelPropertyChanged;
        };
        DataContextChanged += (_, _) => { if (_attached) Hook(); };
    }

    void Hook()
    {
        if (_hooked is not null) _hooked.PropertyChanged -= OnViewModelPropertyChanged;
        _hooked = DataContext as StacksViewModel;
        if (_hooked is not null) _hooked.PropertyChanged += OnViewModelPropertyChanged;
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StacksViewModel.IsCreating)) SyncEditorWindow();
    }

    void SyncEditorWindow()
    {
        if (_hooked is { IsCreating: true }) OpenEditor();
        else CloseEditor();
    }

    void OpenEditor()
    {
        if (!_attached || _editor is not null || _hooked is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner || owner.PlatformImpl is null) return;

        var window = new StackEditorWindow { DataContext = _hooked };

        // Hand the builder's coach layer the shared run so a tour step can spotlight the canvas / Create
        // button inside this window, and dim the main window's layer so only one coachmark shows.
        if (owner.DataContext is MainWindowViewModel main)
        {
            window.AttachCoach(main.Coach);
            _mainCoach = owner.FindControl<CoachOverlay>("Coachmarks");
            if (_mainCoach is not null) _mainCoach.Suppressed = true;
        }

        window.Closed += OnEditorClosed;
        _editor = window;
        window.Show(owner);
    }

    void CloseEditor()
    {
        if (_editor is not { } window) return;
        window.Closed -= OnEditorClosed; // closing it ourselves — don't re-enter via the reconcile path
        _editor = null;
        RestoreMainCoach();
        window.Close();
    }

    // The window was closed by the user (chrome / Escape closed it directly): reconcile the view model so
    // it doesn't think the builder is still open.
    void OnEditorClosed(object? sender, EventArgs e)
    {
        _editor = null;
        RestoreMainCoach();
        if (_hooked is { IsCreating: true } vm && vm.CancelCreateCommand.CanExecute(null))
            vm.CancelCreateCommand.Execute(null);
    }

    void RestoreMainCoach()
    {
        if (_mainCoach is not null) { _mainCoach.Suppressed = false; _mainCoach = null; }
    }

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
