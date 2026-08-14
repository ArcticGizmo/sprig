using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Stacks;

namespace Sprig.App.ViewModels;

/// <summary>How an input's value is being supplied — the structured choice the graph's per-repo editor
/// offers, with <see cref="Custom"/> the escape hatch for templates and multi-source transforms.</summary>
public enum InputSourceKind { Port, Workspace, Literal, Custom }

/// <summary>
/// The modal that edits one repo's input bindings from the repo graph — a row per declared input, each
/// set through a structured picker (a port, the workspace, a literal, or a custom expression) rather
/// than by dragging wires. It edits the very same <see cref="BindingRow"/>s the patchbay does, so every
/// change flows through the stack view model's existing rebuild and the graph's lines redraw live
/// behind it. Opened by <see cref="StacksViewModel.EditRepoInputsCommand"/>; closed via
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
        // The custom editor autosuggests the same tokens the patchbay does: the workspace + each port.
        Variables = new ObservableCollection<string> { "workspace" };
        foreach (var p in declaredPorts) Variables.Add("ports." + p);

        foreach (var row in group.Rows)
            Rows.Add(new RepoInputRowViewModel(row, AvailablePorts, Variables, declaredPorts, DeclarePort));
    }

    /// <summary>
    /// Declare a port (or reuse one of the same name) and make it selectable everywhere in this modal —
    /// the shared machinery behind both the footer "＋ add port" and a row's inline "new port". Returns
    /// the canonical name so a caller can immediately bind to it; null when the name is blank.
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

    /// <summary>Ports the Port dropdowns offer — the editor's own copy, grown as new ports are minted.</summary>
    public ObservableCollection<string> AvailablePorts { get; }

    /// <summary>Autosuggest tokens for the custom-expression editor (workspace + <c>ports.*</c>).</summary>
    public ObservableCollection<string> Variables { get; }

    [ObservableProperty] private string _newPortName = "";

    /// <summary>Mint a new stack port so it can be picked here (footer action — declares without binding).</summary>
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
/// One input row in the repo editor: it wraps the real <see cref="BindingRow"/> (the single source of
/// truth) and projects its expression into the structured picker — kind + value — writing a composed
/// expression straight back on any change. Because it mutates the same row the rest of the app holds,
/// the graph and the patchbay stay in lock-step with what's typed here.
/// </summary>
public sealed partial class RepoInputRowViewModel : ViewModelBase
{
    readonly BindingRow _row;
    readonly PropertyChangedEventHandler _onRowChanged;
    readonly Func<string, string?> _declarePort;
    bool _suppress;    // true while we're mirroring the expression INTO the fields (don't write back)
    bool _composing;   // true while WE write the expression (so our own write doesn't re-sync and bounce)

    public RepoInputRowViewModel(BindingRow row, ObservableCollection<string> availablePorts,
        ObservableCollection<string> variables, IReadOnlyList<string> declaredPorts, Func<string, string?> declarePort)
    {
        _row = row;
        AvailablePorts = availablePorts;
        Variables = variables;
        _declared = declaredPorts;
        _declarePort = declarePort;
        SyncFromExpression();
        // React only to EXTERNAL edits (e.g. the patchbay rewiring the same row). Our own composed writes
        // are guarded by _composing, so choosing "Literal" and leaving it blank doesn't bounce back to Port.
        _onRowChanged = (_, e) => { if (e.PropertyName == nameof(BindingRow.Expression) && !_composing) SyncFromExpression(); };
        _row.PropertyChanged += _onRowChanged;
    }

    readonly IReadOnlyList<string> _declared;

    public string Input => _row.Input;
    public string? Example => _row.Example;

    /// <summary>The composed expression currently on the underlying binding (the single source of truth).</summary>
    public string RawExpression => _row.Expression;
    public ObservableCollection<string> AvailablePorts { get; }
    public ObservableCollection<string> Variables { get; }

    /// <summary>The picker options, in the order the modal shows them.</summary>
    public IReadOnlyList<InputSourceKind> Kinds { get; } =
        [InputSourceKind.Port, InputSourceKind.Workspace, InputSourceKind.Literal, InputSourceKind.Custom];

    [ObservableProperty] private InputSourceKind _kind;
    [ObservableProperty] private string? _portName;
    [ObservableProperty] private string _literalValue = "";
    [ObservableProperty] private string _customExpression = "";

    public bool IsPort => Kind == InputSourceKind.Port;
    public bool IsWorkspace => Kind == InputSourceKind.Workspace;
    public bool IsLiteral => Kind == InputSourceKind.Literal;
    public bool IsCustom => Kind == InputSourceKind.Custom;

    /// <summary>True while the inline "new port" name box is showing (in place of the port dropdown).</summary>
    [ObservableProperty] private bool _addingPort;
    [ObservableProperty] private string _newPortName = "";

    /// <summary>Show the inline "name a new port" box — the first-run answer to "there's no port yet".</summary>
    [RelayCommand]
    private void StartAddPort() { NewPortName = ""; AddingPort = true; }

    /// <summary>Create the named port and bind this input straight to it.</summary>
    [RelayCommand]
    private void ConfirmAddPort()
    {
        var created = _declarePort(NewPortName);
        AddingPort = false;
        NewPortName = "";
        if (created is { Length: > 0 })
        {
            Kind = InputSourceKind.Port;
            PortName = created;   // composes ${sprig.ports.<created>} onto the binding
        }
    }

    [RelayCommand]
    private void CancelAddPort() { AddingPort = false; NewPortName = ""; }

    partial void OnKindChanged(InputSourceKind value)
    {
        OnPropertyChanged(nameof(IsPort));
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsLiteral));
        OnPropertyChanged(nameof(IsCustom));
        // Switching to Port with nothing chosen yet: default to the first available so the line has a
        // target rather than blanking the binding.
        if (!_suppress && value == InputSourceKind.Port && string.IsNullOrEmpty(PortName) && AvailablePorts.Count > 0)
            PortName = AvailablePorts[0];
        Compose();
    }

    partial void OnPortNameChanged(string? value) { if (Kind == InputSourceKind.Port) Compose(); }
    partial void OnLiteralValueChanged(string value) { if (Kind == InputSourceKind.Literal) Compose(); }
    partial void OnCustomExpressionChanged(string value) { if (Kind == InputSourceKind.Custom) Compose(); }

    /// <summary>Write the composed expression back to the real binding row (unless we're mid-sync).</summary>
    void Compose()
    {
        if (_suppress) return;
        _composing = true;
        _row.Expression = Kind switch
        {
            InputSourceKind.Workspace => "${sprig.workspace}",
            InputSourceKind.Port => PortName is { Length: > 0 } p ? $"${{sprig.ports.{p}}}" : "",
            InputSourceKind.Literal => LiteralValue,
            _ => CustomExpression,
        };
        _composing = false;
    }

    /// <summary>Classify the current expression back into the structured fields (kind + value).</summary>
    void SyncFromExpression()
    {
        _suppress = true;
        var expr = (_row.Expression ?? "").Trim();
        var ports = PortExpressions.ReferencedPorts(expr).Where(_declared.Contains).ToList();
        var usesWorkspace = PortExpressions.ReferencesWorkspace(expr);

        if (expr.Length == 0)
        {
            // Unbound: default to the Port picker with nothing chosen, so a first-time author lands on
            // "pick or create a port" rather than an empty literal box — and the binding stays unset
            // until they actually choose one.
            Kind = InputSourceKind.Port;
            PortName = null;
        }
        else if (expr == "${sprig.workspace}")
        {
            Kind = InputSourceKind.Workspace;
        }
        else if (ports.Count == 1 && !usesWorkspace && expr == $"${{sprig.ports.{ports[0]}}}")
        {
            Kind = InputSourceKind.Port;
            PortName = ports[0];
        }
        else if (ports.Count == 0 && !usesWorkspace)
        {
            Kind = InputSourceKind.Literal;
            LiteralValue = _row.Expression ?? "";
        }
        else
        {
            Kind = InputSourceKind.Custom;
            CustomExpression = _row.Expression ?? "";
        }

        OnPropertyChanged(nameof(IsPort));
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsLiteral));
        OnPropertyChanged(nameof(IsCustom));
        _suppress = false;
    }

    public void Detach() => _row.PropertyChanged -= _onRowChanged;
}
