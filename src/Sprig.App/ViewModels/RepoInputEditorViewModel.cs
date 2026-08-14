using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// The modal that edits one repo's input bindings from the repo graph — a row per declared input, each
/// edited with the same token box (autocomplete + highlighting) used everywhere else, plus a shortcut to
/// mint a new port and reference it. It edits the very same <see cref="BindingRow"/>s the patchbay does,
/// so every change flows through the stack view model's existing rebuild and the graph's pins and lines
/// redraw live behind it. Opened by <see cref="StacksViewModel.EditRepoInputsCommand"/>; closed via
/// <see cref="CloseRequested"/>.
/// </summary>
public sealed partial class RepoInputEditorViewModel : ViewModelBase
{
    readonly Action<string> _createPort;

    /// <summary>Raised when the modal asks to close (✕ / Done); the parent nulls its reference.</summary>
    public event Action? CloseRequested;

    public RepoInputEditorViewModel(RepoBindingGroup group, IReadOnlyList<string> declaredPorts, Action<string> createPort)
    {
        RepoName = group.Repo;
        _createPort = createPort;
        AvailablePorts = new ObservableCollection<string>(declaredPorts);
        // The token box autocompletes the same tokens the patchbay does: the workspace + each port.
        Variables = new ObservableCollection<string> { "workspace" };
        foreach (var p in declaredPorts) Variables.Add("ports." + p);

        foreach (var row in group.Rows)
            Rows.Add(new RepoInputRowViewModel(row, Variables, DeclarePort));
    }

    /// <summary>
    /// Declare a port (or reuse one of the same name) and make it autocompletable everywhere in this
    /// modal — the shared machinery behind both the footer "＋ add port" and a row's inline "＋ port".
    /// Returns the canonical name so a caller can immediately reference it; null when the name is blank.
    /// </summary>
    string? DeclarePort(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return null;
        if (!AvailablePorts.Contains(n))
        {
            _createPort(n);                 // the parent declares it (and rebuilds the graph)
            AvailablePorts.Add(n);
            Variables.Add("ports." + n);
        }
        return n;
    }

    public string RepoName { get; }
    public ObservableCollection<RepoInputRowViewModel> Rows { get; } = [];

    /// <summary>Ports that already exist — kept so declaring a duplicate is a no-op.</summary>
    public ObservableCollection<string> AvailablePorts { get; }

    /// <summary>Autocomplete tokens for every row's token box (workspace + <c>ports.*</c>).</summary>
    public ObservableCollection<string> Variables { get; }

    [ObservableProperty] private string _newPortName = "";

    /// <summary>Mint a new stack port so it can be referenced here (footer action — declares without binding).</summary>
    [RelayCommand]
    private void AddPort()
    {
        DeclarePort(NewPortName);
        NewPortName = "";
    }

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
/// what the patchbay shows stay in lock-step. It also carries the inline "＋ port" flow: name a new port
/// and this input is bound straight to it.
/// </summary>
public sealed partial class RepoInputRowViewModel : ViewModelBase
{
    readonly BindingRow _row;
    readonly PropertyChangedEventHandler _onRowChanged;
    readonly Func<string, string?> _declarePort;

    public RepoInputRowViewModel(BindingRow row, ObservableCollection<string> variables, Func<string, string?> declarePort)
    {
        _row = row;
        Variables = variables;
        _declarePort = declarePort;
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

    /// <summary>True while the inline "name a new port" box is showing in place of the token box.</summary>
    [ObservableProperty] private bool _addingPort;
    [ObservableProperty] private string _newPortName = "";

    /// <summary>Show the inline "name a new port" box — the answer to "there's no port to reference yet".</summary>
    [RelayCommand]
    private void StartAddPort() { NewPortName = ""; AddingPort = true; }

    /// <summary>Declare the named port and reference it straight from this input.</summary>
    [RelayCommand]
    private void ConfirmAddPort()
    {
        var created = _declarePort(NewPortName);
        AddingPort = false;
        NewPortName = "";
        if (created is { Length: > 0 }) Expression = $"${{sprig.ports.{created}}}";
    }

    [RelayCommand]
    private void CancelAddPort() { AddingPort = false; NewPortName = ""; }

    public void Detach() => _row.PropertyChanged -= _onRowChanged;
}
