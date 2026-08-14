using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// The modal that edits one repo's input bindings from the repo graph — a row per declared input, each
/// edited with the same token box (autocomplete + highlighting) used everywhere else. It edits the very
/// same <see cref="BindingRow"/>s the patchbay does, so every change flows through the stack view model's
/// rebuild and the graph's pins and lines redraw live behind it. Ports themselves are declared on the
/// main editor screen (the "Defined ports" panel), not here — an input may reference a not-yet-declared
/// port, which the token box shows in red until it's accepted there. Opened by
/// <see cref="StacksViewModel.EditRepoInputsCommand"/>; closed via <see cref="CloseRequested"/>.
/// </summary>
public sealed partial class RepoInputEditorViewModel : ViewModelBase
{
    /// <summary>Raised when the modal asks to close (✕ / Done); the parent nulls its reference.</summary>
    public event Action? CloseRequested;

    public RepoInputEditorViewModel(RepoBindingGroup group, IReadOnlyList<string> declaredPorts)
    {
        RepoName = group.Repo;
        // The token box autocompletes the same tokens the patchbay does: the workspace + each port.
        Variables = new ObservableCollection<string> { "workspace" };
        foreach (var p in declaredPorts) Variables.Add("ports." + p);

        foreach (var row in group.Rows)
            Rows.Add(new RepoInputRowViewModel(row, Variables));
    }

    public string RepoName { get; }
    public ObservableCollection<RepoInputRowViewModel> Rows { get; } = [];

    /// <summary>Autocomplete tokens for every row's token box (workspace + <c>ports.*</c>).</summary>
    public ObservableCollection<string> Variables { get; }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>Stop tracking the underlying binding rows (called when the modal closes).</summary>
    public void Detach()
    {
        foreach (var row in Rows) row.Detach();
    }
}

/// <summary>
/// One input row in the repo editor. It wraps the real <see cref="BindingRow"/> (the single source of
/// truth) and exposes its expression for direct editing through the token box, so what's typed here and
/// what the patchbay shows stay in lock-step.
/// </summary>
public sealed partial class RepoInputRowViewModel : ViewModelBase
{
    readonly BindingRow _row;
    readonly PropertyChangedEventHandler _onRowChanged;

    public RepoInputRowViewModel(BindingRow row, ObservableCollection<string> variables)
    {
        _row = row;
        Variables = variables;
        // Reflect external edits (e.g. the patchbay rewiring the same row) back into the token box.
        _onRowChanged = (_, e) => { if (e.PropertyName == nameof(BindingRow.Expression)) OnPropertyChanged(nameof(Expression)); };
        _row.PropertyChanged += _onRowChanged;
    }

    public string Input => _row.Input;
    public string? Example => _row.Example;
    public ObservableCollection<string> Variables { get; }

    /// <summary>The full expression, edited directly through the token box (the single source of truth).</summary>
    public string? Expression
    {
        get => _row.Expression;
        set { if (_row.Expression != (value ?? "")) _row.Expression = value ?? ""; }
    }

    public void Detach() => _row.PropertyChanged -= _onRowChanged;
}
