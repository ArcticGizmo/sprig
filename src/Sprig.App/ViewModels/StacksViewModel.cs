using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Controls;
using Sprig.Core.Config;
using Sprig.Core.Stacks;

namespace Sprig.App.ViewModels;

public partial class StacksViewModel : PageViewModel
{
    protected readonly AppServices Services;
    readonly Navigator _nav;

    /// <summary>
    /// True while auto-wire is writing, so the row change handlers don't mistake its own edits for the user
    /// taking ownership of a port or a binding.
    /// </summary>
    bool _applyingAutoWire;

    public StacksViewModel(AppServices services, Navigator nav)
    {
        Services = services;
        _nav = nav;
        Reload();
        // The store changing elsewhere affects two things here: repos added/removed on the Repos
        // tab must appear in (or drop out of) the builder's repo picker, and workspaces
        // created/removed change the edit gate for the selected stack.
        Services.StoreChanged += OnStoreChanged;
    }

    protected override void OnActivated() => OnStoreChanged();

    /// <summary>React to any store change: pick up repo registry edits, then re-gate editing.</summary>
    void OnStoreChanged()
    {
        SyncRepoChoices();
        RefreshAttached();
    }

    public override string Title => "Stacks";

    public ObservableCollection<StackDefinition> Stacks { get; } = [];
    public ObservableCollection<RepoChoiceViewModel> RepoChoices { get; } = [];

    /// <summary>False when no stacks are defined yet — drives the first-run empty state.</summary>
    public bool HasStacks => Stacks.Count > 0;

    /// <summary>False when no repos are registered — a stack can't be built without one (upstream empty state).</summary>
    public bool HasAnyRepos => RepoChoices.Count > 0;

    /// <summary>Empty-state shortcut: jump to Repos and open Add.</summary>
    [RelayCommand] private void AddRepo() => _nav.AddRepo();

    /// <summary>The stack's named ports (auto-allocated at create; shown with an incrementing preview).</summary>
    public ObservableCollection<StackPortRow> Ports { get; } = [];

    /// <summary>Per-repo input bindings for the selected repos.</summary>
    public ObservableCollection<RepoBindingGroup> Bindings { get; } = [];

    /// <summary>The tokens the binding expressions can autosuggest: <c>workspace</c> + one per named port.</summary>
    public ObservableCollection<string> BindingVariables { get; } = ["workspace"];

    /// <summary>Just the named ports — the choices in each row's "share a port" picker.</summary>
    public ObservableCollection<string> PortNames { get; } = [];
    public bool HasPorts => PortNames.Count > 0;

    /// <summary>Registered repos not yet in the stack — the canvas "add repo" slot lists these.</summary>
    public ObservableCollection<string> AddableRepos { get; } = [];

    [ObservableProperty] private StackDefinition? _selected;

    /// <summary>The stack name — path-compatible, used as the filename/key, worktree folder and branch.</summary>
    [ObservableProperty] private string _newName = "";

    /// <summary>Pool capacity (<c>MaxSlots</c>) as typed — the most workspaces this stack's pool may hold
    /// at once. Held as text (like the port-range fields) so a mid-edit blank doesn't throw; parsed on save.</summary>
    [ObservableProperty] private string _newCapacity = StackDefinition.DefaultMaxSlots.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isCreating;

    /// <summary>Set when an import (or export) fails — shown as a banner, with a shortcut to register repos.</summary>
    [ObservableProperty] private string? _importError;

    /// <summary>Live validation of the name — non-null while it contains a disallowed character.</summary>
    public string? NameError { get; private set; }
    public bool HasNameError => NameError is not null;

    partial void OnNewNameChanged(string value)
    {
        NameError = ValidateName(value);
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(HasNameError));
    }

    /// <summary>Live validation of the capacity — non-null while it isn't a positive whole number.</summary>
    public string? CapacityError { get; private set; }
    public bool HasCapacityError => CapacityError is not null;

    partial void OnNewCapacityChanged(string value)
    {
        CapacityError = ValidateCapacity(value);
        OnPropertyChanged(nameof(CapacityError));
        OnPropertyChanged(nameof(HasCapacityError));
    }

    /// <summary>Null when the capacity is a positive whole number (or still empty); otherwise the reason.
    /// The real ceiling (ports × capacity fitting the range) is enforced by <c>StackStore.Save</c>; this
    /// is just the "is it a sensible number" gate so the field can't push a zero or a word into save.</summary>
    static string? ValidateCapacity(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1
            ? null
            : "Capacity must be a whole number of 1 or more.";
    }

    /// <summary>
    /// Null when the name is valid (or still empty); otherwise a message naming the bad character(s).
    /// Mirrors StackStore's <c>^[A-Za-z0-9._+-]+$</c> rule so "valid here" matches "valid on save".
    /// </summary>
    static string? ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var invalid = name
            .Where(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-'))
            .Distinct()
            .Select(c => char.IsWhiteSpace(c) ? "space" : c.ToString())
            .ToList();
        return invalid.Count == 0
            ? null
            : $"Can't use: {string.Join("  ", invalid)}  —  only letters, numbers, and . _ + -";
    }

    // --- Selection detail + edit gating -------------------------------------

    /// <summary>How many live workspaces were created from the selected stack (drives the edit gate).</summary>
    [ObservableProperty] private int _attachedWorkspaces;

    /// <summary>Original name while editing an existing stack; null when creating a new one.</summary>
    [ObservableProperty] private string? _editingOriginalName;

    /// <summary>True while the remove-stack confirm bar is showing.</summary>
    [ObservableProperty] private bool _confirmingRemove;

    /// <summary>True while the clone-stack name prompt is showing.</summary>
    [ObservableProperty] private bool _cloningStack;

    /// <summary>The name typed into the clone prompt (pre-filled with a unique suggestion).</summary>
    [ObservableProperty] private string _cloneName = "";

    /// <summary>The stack being cloned — its full definition is copied under the new name.</summary>
    StackDefinition? _cloneSource;

    /// <summary>A clone that failed to save (rare — the name is pre-validated); surfaced in the prompt.</summary>
    [ObservableProperty] private string? _cloneError;

    /// <summary>Live validation of the clone name — names a bad character or a name already in use.</summary>
    public string? CloneNameError { get; private set; }
    public bool HasCloneNameError => CloneNameError is not null;

    partial void OnCloneNameChanged(string value)
    {
        CloneError = null;
        CloneNameError = ValidateCloneName(value);
        OnPropertyChanged(nameof(CloneNameError));
        OnPropertyChanged(nameof(HasCloneNameError));
        ConfirmCloneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Null when the trimmed name is a valid, unused stack name; otherwise the reason. Empty reads as
    /// "not yet" (no error shown) but still blocks Clone. Layered on <see cref="ValidateName"/> so the
    /// character rule matches everywhere, plus a collision check — a clone must never overwrite a stack.
    /// </summary>
    string? ValidateCloneName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        if (ValidateName(trimmed) is { } charError) return charError;
        // Stack names are filenames (case-insensitive on Windows), so collide case-insensitively.
        if (Stacks.Any(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"a stack named '{trimmed}' already exists";
        return null;
    }

    /// <summary>The selected stack's per-repo bindings, flattened for the detail panel.</summary>
    public ObservableCollection<StackBindingView> DetailBindings { get; } = [];

    // --- The builder's live wiring (the canvas is the one and only build surface) ----------------

    /// <summary>The wiring graph for the in-progress build — rebuilt on every binding/port change.</summary>
    [ObservableProperty] private WiringGraph? _builderWiring;

    /// <summary>
    /// The repo-centric view of the in-progress build — the same wiring seen as repos with directed
    /// owner→consumer dependency lines and shared-port chips. The second lens on the same edit surface.
    /// </summary>
    [ObservableProperty] private RepoGraph? _builderRepoGraph;

    /// <summary>False shows the port-centric patchbay; true shows the repo dependency graph.</summary>
    [ObservableProperty] private bool _graphView;

    /// <summary>
    /// Which repo owns (produces) each stack port — port → repo. A visualization-only overlay (it never
    /// feeds resolution) that decides whether a port draws as a directed owner→consumer line or a shared
    /// chip on the repo graph. Held on the side rather than on a port row because it's a cross-repo
    /// relationship, not a property of one port; persisted to <see cref="StackDefinition.Owners"/> on
    /// save and pruned to the live ports/repos on every rebuild.
    /// </summary>
    readonly Dictionary<string, string> _portOwners = new(StringComparer.Ordinal);

    /// <summary>Rebuild the live graphs the builder's canvases draw from the current rows + ports.</summary>
    void RebuildBuilderWiring()
    {
        var repos = Bindings.Select(g => g.Repo).ToList();
        var ports = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();
        var inputs = Bindings.ToDictionary(
            g => g.Repo, g => (IReadOnlyList<string>)g.Rows.Select(r => r.Input).ToList());
        var bindings = Bindings.ToDictionary(
            g => g.Repo, g => (IReadOnlyDictionary<string, string>)g.Rows.ToDictionary(r => r.Input, r => r.Expression));
        BuilderWiring = WiringGraph.Build(repos, ports, inputs, bindings);

        // Drop ownership picks whose port or repo has since gone, so a stale owner never lingers in the
        // graph (or gets written on save), then rebuild the repo-centric view from the same data.
        var portSet = new HashSet<string>(ports, StringComparer.Ordinal);
        var repoSet = new HashSet<string>(repos, StringComparer.Ordinal);
        foreach (var port in _portOwners.Keys.ToList())
            if (!portSet.Contains(port) || !repoSet.Contains(_portOwners[port]))
                _portOwners.Remove(port);
        BuilderRepoGraph = RepoGraph.Build(repos, ports, inputs, bindings, _portOwners);
    }

    /// <summary>Assign or clear a stack port's owning repo on the repo graph (null repo clears it).</summary>
    [RelayCommand]
    private void SetPortOwner(SetPortOwnerRequest? request)
    {
        if (request is null) return;
        var port = request.Port.Trim();
        if (port.Length == 0) return;
        if (request.Repo is { Length: > 0 } repo) _portOwners[port] = repo;
        else _portOwners.Remove(port);
        RebuildBuilderWiring();
    }

    /// <summary>
    /// Fill in owners for the ports that don't have one, inferred from their names (<c>api_port</c> →
    /// <c>api</c>). A conservative assist — it only ever fills blanks, never overrides an owner you
    /// picked, and skips a port when the name is ambiguous — so it's safe to run and then review on the
    /// graph. No-op when nothing new can be guessed.
    /// </summary>
    [RelayCommand]
    private void GuessOwners()
    {
        var repos = Bindings.Select(g => g.Repo).ToList();
        var ports = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();
        var proposals = StackOwnerGuess.Guess(repos, ports, _portOwners);
        if (proposals.Count == 0) return;
        foreach (var (port, repo) in proposals) _portOwners[port] = repo;
        RebuildBuilderWiring();
    }

    BindingRow? FindRow(string repo, string input) =>
        Bindings.FirstOrDefault(g => g.Repo == repo)?.Rows.FirstOrDefault(r => r.Input == input);

    /// <summary>The raw token that binds an input to a stack port.</summary>
    static string PortToken(string port) => $"${{sprig.ports.{port}}}";

    /// <summary>Bind an input to a port (drag a port → input). Replaces any current binding.</summary>
    [RelayCommand]
    private void WirePin(WireRequest? request)
    {
        if (request is null) return;
        if (FindRow(request.Repo, request.Input) is { } row) row.Expression = PortToken(request.Port);
    }

    /// <summary>Clear an input's binding (drag pin → empty space).</summary>
    [RelayCommand]
    private void UnwirePin(PinRef? pin)
    {
        if (pin is null) return;
        if (FindRow(pin.Repo, pin.Input) is { } row) row.Expression = "";
    }

    /// <summary>Bind an input to the workspace source (drag the workspace chip → input). Replaces any current value.</summary>
    [RelayCommand]
    private void WireWorkspace(PinRef? pin)
    {
        if (pin is null) return;
        if (FindRow(pin.Repo, pin.Input) is { } row) row.Expression = "${sprig.workspace}";
    }

    /// <summary>Set an input's expression directly (inline editor on an input or transform node).</summary>
    [RelayCommand]
    private void SetExpression(SetExpressionRequest? request)
    {
        if (request is null) return;
        if (FindRow(request.Repo, request.Input) is { } row) row.Expression = request.Expression;
    }

    /// <summary>
    /// Append a source token to an input's expression — dragging a second port (or the workspace)
    /// into an existing transform node to fan it in. A token already present is left alone.
    /// </summary>
    [RelayCommand]
    private void AppendSource(AppendSourceRequest? request)
    {
        if (request is null) return;
        if (FindRow(request.Repo, request.Input) is not { } row) return;
        if (row.Expression.Contains(request.Token, StringComparison.Ordinal)) return;
        row.Expression += request.Token;
    }

    /// <summary>Add a named stack port from the canvas (click the "create new…" slot with no drag).</summary>
    [RelayCommand]
    private void AddNamedPort(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0 || Ports.Any(p => p.Name.Trim() == n)) return;
        Ports.Add(NewPortRow(n));
        ReindexPortPreviews();
        RebuildBindingVariables();
        RebuildBuilderWiring();
    }

    /// <summary>Rename a stack port from the canvas; the row's own change handler propagates it to bindings.</summary>
    [RelayCommand]
    private void RenamePort(RenamePortRequest? request)
    {
        if (request is null) return;
        var newName = request.NewName.Trim();
        if (newName.Length == 0 || newName == request.OldName) return;
        if (Ports.Any(p => p.Name.Trim() == newName)) return; // don't collide with an existing port
        if (Ports.FirstOrDefault(p => p.Name.Trim() == request.OldName) is { } row) row.Name = newName;
    }

    /// <summary>Remove a stack port by name from the canvas (its cables drop away with it).</summary>
    [RelayCommand]
    private void RemoveNamedPort(string? name)
    {
        var n = name?.Trim() ?? "";
        if (Ports.FirstOrDefault(p => p.Name.Trim() == n) is { } row) RemovePort(row);
    }

    /// <summary>
    /// Create a new stack port and wire an input to it — the drop from the phantom "create new…" slot,
    /// once the user names the port. A name that already exists is reused rather than duplicated, so
    /// "create new" typed with an existing name simply shares that port.
    /// </summary>
    [RelayCommand]
    private void CreatePort(CreatePortRequest? request)
    {
        if (request is null) return;
        var name = request.PortName.Trim();
        if (name.Length == 0) return;

        if (Ports.All(p => p.Name.Trim() != name))
        {
            Ports.Add(NewPortRow(name));
            ReindexPortPreviews();
            RebuildBindingVariables();
        }

        if (FindRow(request.Repo, request.Input) is { } row) row.Expression = PortToken(name);
        RebuildBuilderWiring();
    }

    public bool HasSelected => Selected is not null;

    /// <summary>Editing is allowed only when no workspaces were built from this stack.</summary>
    public bool CanEditSelected => Selected is not null && AttachedWorkspaces == 0;
    public bool EditBlocked => Selected is not null && AttachedWorkspaces > 0;

    public string? EditBlockedReason => EditBlocked
        ? $"{AttachedWorkspaces} workspace{(AttachedWorkspaces == 1 ? "" : "s")} use this stack — remove them before editing."
        : null;

    public bool IsEditing => EditingOriginalName is not null;
    public string OverlayTitle => IsEditing ? "Edit stack" : "New stack";
    public string OverlayCta => IsEditing ? "Save changes" : "Create stack";

    partial void OnEditingOriginalNameChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(OverlayTitle));
        OnPropertyChanged(nameof(OverlayCta));
    }

    partial void OnSelectedChanged(StackDefinition? value)
    {
        ConfirmingRemove = false;
        // A clone prompt belongs to the stack it was opened on — moving off that stack dismisses it.
        CloningStack = false;
        CloneError = null;
        DetailBindings.Clear();
        if (value is not null)
            foreach (var repo in value.Repos)
            {
                var rows = value.Bindings.TryGetValue(repo, out var b)
                    ? b.OrderBy(kv => kv.Key).Select(kv => new StackBindingRowView(kv.Key, kv.Value)).ToList()
                    : new List<StackBindingRowView>();
                DetailBindings.Add(new StackBindingView(repo, rows));
            }

        OnPropertyChanged(nameof(HasSelected));
        RefreshAttached();
    }

    /// <summary>Recompute how many workspaces use the selected stack.</summary>
    void RefreshAttached()
    {
        AttachedWorkspaces = Selected is null
            ? 0
            : Services.Workspaces.List().Count(w => w.Stack == Selected.Name);
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(EditBlocked));
        OnPropertyChanged(nameof(EditBlockedReason));
    }

    /// <summary>Open the builder pre-filled with the selected stack (only when nothing depends on it).</summary>
    [RelayCommand]
    private void EditSelected()
    {
        var stack = Selected;
        if (stack is null || !CanEditSelected) return;

        CloningStack = false;
        Error = null; Status = null;
        EditingOriginalName = stack.Name;
        NewName = stack.Name;
        NewCapacity = stack.MaxSlots.ToString(CultureInfo.InvariantCulture);

        foreach (var row in Ports) row.PropertyChanged -= OnPortRowChanged;
        Ports.Clear();
        foreach (var p in stack.Ports) Ports.Add(NewPortRow(p));
        ReindexPortPreviews();
        RebuildBindingVariables();

        // Reset then check the stack's repos, so RecomputeBindingGroups rebuilds a clean set of rows.
        foreach (var c in RepoChoices) c.IsSelected = false;
        foreach (var c in RepoChoices) c.IsSelected = stack.Repos.Contains(c.Name);

        // The picker is in registry order, so the binding groups arrive that way. Reorder them to match
        // the stack's saved repo order, so the canvas opens showing exactly the order that was saved.
        var savedOrder = stack.Repos.Where(r => Bindings.Any(g => g.Repo == r)).ToList();
        for (var target = 0; target < savedOrder.Count; target++)
        {
            var current = -1;
            for (var i = 0; i < Bindings.Count; i++) if (Bindings[i].Repo == savedOrder[target]) { current = i; break; }
            if (current >= 0 && current != target) Bindings.Move(current, target);
        }

        foreach (var group in Bindings)
            if (stack.Bindings.TryGetValue(group.Repo, out var repoBindings))
                foreach (var row in group.Rows)
                    if (repoBindings.TryGetValue(row.Input, out var expr))
                        row.Expression = expr;

        // Restore the ownership overlay, then rebuild so the graph opens showing exactly what was saved.
        _portOwners.Clear();
        foreach (var o in stack.Owners) _portOwners[o.Port] = o.Repo;
        RebuildBuilderWiring();

        IsCreating = true;
    }

    /// <summary>Open the builder with a fresh, empty form for a new stack.</summary>
    [RelayCommand]
    private void NewStack()
    {
        CloningStack = false;
        EditingOriginalName = null;
        NewName = "";
        NewCapacity = StackDefinition.DefaultMaxSlots.ToString(CultureInfo.InvariantCulture);
        foreach (var c in RepoChoices) c.IsSelected = false;
        foreach (var row in Ports) row.PropertyChanged -= OnPortRowChanged;
        Ports.Clear();
        Bindings.Clear();
        _portOwners.Clear();
        RebuildBindingVariables();
        // Rebuild the (now empty) graph so the canvas doesn't keep drawing the last session's ports.
        RebuildBuilderWiring();
        Error = null; Status = null; ImportError = null;
        IsCreating = true;
    }

    // --- Import / export ----------------------------------------------------
    // Stacks live in the central store (a cross-repo concern), not in any repo. Export copies a stack's
    // JSON out for sharing; import reads one back and saves it — but only once every repo it names is
    // registered on this machine, since stacks reference repos by name (paths stay machine-local).
    // The file picking needs the window's TopLevel, so the view code-behind picks the path and calls these.

    /// <summary>Write the selected stack's JSON to the chosen path.</summary>
    public void ExportTo(string path)
    {
        if (Selected is null) return;
        Error = null; ImportError = null;
        try
        {
            var written = Services.Stacks.Export(Selected.Name, path);
            Status = $"exported '{Selected.Name}' to {written}";
        }
        catch (Exception ex) { Status = null; ImportError = ex.Message; }
    }

    /// <summary>Read a stack JSON from the chosen path, validate it against the registry, and save it.</summary>
    public void ImportFrom(string path)
    {
        Error = null; Status = null; ImportError = null;
        try
        {
            var imported = Services.Stacks.Import(path);
            Reload();
            Selected = Stacks.FirstOrDefault(s => s.Name == imported.Name);
            Status = $"imported stack '{imported.Name}'";
            Services.NotifyStoreChanged();
        }
        catch (Exception ex) { ImportError = ex.Message; }
    }

    [RelayCommand]
    private void CancelCreate() { IsCreating = false; Error = null; }

    [RelayCommand]
    private void AddPort()
    {
        Ports.Add(NewPortRow());
        ReindexPortPreviews();
        RebuildBindingVariables();
        RebuildBuilderWiring();
    }

    [RelayCommand]
    private void RemovePort(StackPortRow row)
    {
        row.PropertyChanged -= OnPortRowChanged;
        Ports.Remove(row);
        ReindexPortPreviews();
        RebuildBindingVariables();
        RebuildBuilderWiring();
    }

    void OnPortRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StackPortRow.Name)) return;

        // Renaming a port rewrites every binding that referenced it, so a rename never silently
        // orphans a wiring. The port name box commits on blur, so this sees one old→new transition.
        if (sender is StackPortRow row)
        {
            var renamed = row.Name.Trim();
            var previous = row.CommittedName;
            if (previous.Length > 0 && renamed.Length > 0 && previous != renamed)
            {
                PropagatePortRename(previous, renamed);
                // Naming a port is an act of intent: it stops being auto-wire's to recompute away.
                if (!_applyingAutoWire) row.Auto = false;
            }
            row.CommittedName = renamed;
        }

        RebuildBindingVariables();
        RebuildBuilderWiring();
    }

    /// <summary>Rewrite every binding expression that references <paramref name="oldName"/> to use the new port name.</summary>
    void PropagatePortRename(string oldName, string newName)
    {
        var oldToken = $"${{sprig.ports.{oldName}}}";
        var newToken = $"${{sprig.ports.{newName}}}";
        foreach (var group in Bindings)
            foreach (var bindingRow in group.Rows)
                if (bindingRow.Expression.Contains(oldToken, StringComparison.Ordinal))
                    bindingRow.Expression = bindingRow.Expression.Replace(oldToken, newToken, StringComparison.Ordinal);

        // Ownership is keyed by port name, so a rename must carry the owner across or the graph would
        // silently orphan it (and the prune would drop it on the next rebuild).
        if (_portOwners.Remove(oldName, out var ownerRepo)) _portOwners[newName] = ownerRepo;
    }

    /// <summary>Create a port row wired for change tracking (rename propagation + previews).</summary>
    /// <param name="auto">True only when auto-wire is proposing this port; every other caller is the user.</param>
    StackPortRow NewPortRow(string name = "", bool auto = false)
    {
        var row = new StackPortRow { Name = name, CommittedName = name, Auto = auto };
        row.PropertyChanged += OnPortRowChanged;
        return row;
    }

    /// <summary>Rebuild the autosuggest tokens and the port picker list from the current named ports.</summary>
    void RebuildBindingVariables()
    {
        BindingVariables.Clear();
        BindingVariables.Add("workspace");
        PortNames.Clear();
        foreach (var p in Ports)
        {
            var n = p.Name.Trim();
            if (n.Length > 0) { BindingVariables.Add("ports." + n); PortNames.Add(n); }
        }
        OnPropertyChanged(nameof(HasPorts));
    }

    [RelayCommand]
    private void Create()
    {
        var name = NewName.Trim();
        // The canvas order (Bindings) is authoritative — a drag-reorder on the board persists here.
        var repos = Bindings.Select(g => g.Repo).ToList();
        var bindings = Bindings.ToDictionary(
            g => g.Repo,
            g => (IReadOnlyDictionary<string, string>)g.Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Expression))
                .ToDictionary(r => r.Input, r => r.Expression.Trim()));

        // Drop ports no binding references — an unwired port would just allocate a number nothing uses.
        var used = bindings.Values
            .SelectMany(b => b.Values)
            .SelectMany(PortExpressions.ReferencedPorts)
            .ToHashSet(StringComparer.Ordinal);
        var ports = Ports.Select(p => p.Name.Trim())
            .Where(n => n.Length > 0 && used.Contains(n))
            .ToList();

        var shares = StackShares.Derive(repos, ports, bindings);

        // The ownership overlay, filtered to what actually survives the save (a port dropped for being
        // unwired, or a repo removed, takes its owner with it). Deterministic order for a stable file.
        var portSet = new HashSet<string>(ports, StringComparer.Ordinal);
        var repoSet = new HashSet<string>(repos, StringComparer.Ordinal);
        var owners = _portOwners
            .Where(kv => portSet.Contains(kv.Key) && repoSet.Contains(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PortOwner { Port = kv.Key, Repo = kv.Value })
            .ToList();

        Error = null; Status = null;

        if (ValidateCapacity(NewCapacity) is { } capacityError) { Error = capacityError; return; }
        // Blank reads as "leave it at the default" rather than an error, matching the field's empty state.
        var maxSlots = int.TryParse(NewCapacity.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : StackDefinition.DefaultMaxSlots;

        try
        {
            // Editing preserves the fields the canvas doesn't own (stack-carried setup, schema) by starting
            // from the stored definition; a new stack starts blank. Either way the canvas-owned fields —
            // repos, ports, bindings, shares, and capacity — are overwritten from the builder.
            var basis = EditingOriginalName is { } editing ? Services.Stacks.Get(editing) : null;
            var definition = (basis ?? new StackDefinition { Name = name }) with
            {
                Name = name, Repos = repos, Ports = ports, Bindings = bindings, Shares = shares,
                Owners = owners, MaxSlots = maxSlots,
            };
            Services.Stacks.Save(definition);

            // Editing with a changed name: the save wrote the new file, so drop the old one.
            var edited = EditingOriginalName;
            if (edited is { } orig && orig != name) Services.Stacks.Remove(orig);

            NewName = "";
            foreach (var c in RepoChoices) c.IsSelected = false;
            Ports.Clear();
            Bindings.Clear();
            _portOwners.Clear();
            EditingOriginalName = null;
            IsCreating = false;
            Status = edited is null ? $"created stack '{name}'" : $"updated stack '{name}'";
            Reload();
            Selected = Stacks.FirstOrDefault(s => s.Name == name);
            Services.NotifyStoreChanged();
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not null) ConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private void ConfirmRemove()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        ConfirmingRemove = false;
        Services.Stacks.Remove(name);
        Status = $"removed stack '{name}'";
        Reload();
        Services.NotifyStoreChanged();
    }

    /// <summary>
    /// Begin cloning a stack (right-click → Clone): select it, pre-fill a unique suggested name, and show
    /// the prompt. Unlike Edit, cloning is never gated on attached workspaces — a clone is a brand-new
    /// stack, so copying one that already has workspaces built from it is perfectly fine.
    /// </summary>
    [RelayCommand]
    private void StartClone(StackDefinition? stack)
    {
        if (stack is null) return;
        _cloneSource = stack;
        Selected = stack;             // OnSelectedChanged clears CloningStack, so raise the prompt after
        CloneError = null;
        CloneName = SuggestCloneName(stack.Name);
        CloningStack = true;
    }

    [RelayCommand]
    private void CancelClone()
    {
        CloningStack = false;
        CloneError = null;
        _cloneSource = null;
    }

    /// <summary>Save a copy of the source stack under the new name, then select the copy.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmClone))]
    private void ConfirmClone()
    {
        if (_cloneSource is null) return;
        var name = CloneName.Trim();

        // Re-read the full definition from the store rather than trust the list item, so a clone reflects
        // whatever is actually on disk at the moment it's taken.
        var source = Services.Stacks.Get(_cloneSource.Name);
        if (source is null) { CloneError = $"stack '{_cloneSource.Name}' no longer exists"; return; }

        CloneError = null; Error = null; Status = null;
        try
        {
            Services.Stacks.Save(source with { Name = name });
            CloningStack = false;
            _cloneSource = null;
            Status = $"cloned to stack '{name}'";
            Reload();
            Selected = Stacks.FirstOrDefault(s => s.Name == name);
            Services.NotifyStoreChanged();
        }
        catch (Exception ex) { CloneError = ex.Message; }
    }

    bool CanConfirmClone() =>
        _cloneSource is not null && CloneName.Trim().Length > 0 && ValidateCloneName(CloneName) is null;

    /// <summary>Suggest a name nothing else uses: "<c>{base}-copy</c>", then "-copy-2", "-copy-3", …</summary>
    string SuggestCloneName(string baseName)
    {
        bool Taken(string n) => Stacks.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
        var candidate = $"{baseName}-copy";
        if (!Taken(candidate)) return candidate;
        for (var i = 2; ; i++)
            if (!Taken($"{baseName}-copy-{i}")) return $"{baseName}-copy-{i}";
    }

    /// <summary>True once there's a graph with more than one port or repo to tidy — gates the button.</summary>
    public bool CanCleanup => BuilderWiring is { } g && (g.Ports.Count > 1 || g.Repos.Count > 1);

    partial void OnBuilderWiringChanged(WiringGraph? value) => OnPropertyChanged(nameof(CanCleanup));

    /// <summary>
    /// Tidy the board to minimise cable crossings: reorder both the source rail and the repos so each
    /// sits near the vertical centre of what it connects to. Pins within a repo stay put (they're its
    /// declared inputs). Only affects layout order — which now persists — never the wiring itself.
    /// No-op when it's already tidy.
    /// </summary>
    [RelayCommand]
    private void Cleanup()
    {
        if (BuilderWiring is not { } g) return;

        var ports = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();
        var repos = Bindings.Select(b => b.Repo).ToList();
        var (orderedPorts, orderedRepos) = WiringCleanup.Tidy(ports, repos, g);

        var portsChanged = ApplyOrder(Ports, orderedPorts, p => p.Name.Trim(), OnPortRowChanged);
        var reposChanged = ApplyOrder(Bindings, orderedRepos, b => b.Repo, handler: null);

        if (portsChanged) ReindexPortPreviews();
        if (portsChanged || reposChanged) RebuildBuilderWiring();
    }

    /// <summary>
    /// Reorder <paramref name="collection"/> so its items follow <paramref name="order"/> (by key);
    /// items whose key isn't in <paramref name="order"/> (e.g. blank port rows) keep their tail spot.
    /// Detaches/reattaches <paramref name="handler"/> around the rebuild if the rows raise change events.
    /// Returns whether anything actually moved.
    /// </summary>
    static bool ApplyOrder<T>(ObservableCollection<T> collection, IReadOnlyList<string> order,
        Func<T, string> key, PropertyChangedEventHandler? handler)
    {
        var rank = order.Select((name, i) => (name, i)).ToDictionary(t => t.name, t => t.i, StringComparer.Ordinal);
        var reordered = collection
            .Select((item, i) => (item, rankKey: rank.TryGetValue(key(item), out var r) ? r : int.MaxValue, original: i))
            .OrderBy(t => t.rankKey)
            .ThenBy(t => t.original) // stable for the untidied tail
            .Select(t => t.item)
            .ToList();

        if (reordered.SequenceEqual(collection)) return false;

        if (handler is not null)
            foreach (var item in collection)
                if (item is INotifyPropertyChanged npc) npc.PropertyChanged -= handler;
        collection.Clear();
        foreach (var item in reordered)
        {
            if (handler is not null && item is INotifyPropertyChanged npc) npc.PropertyChanged += handler;
            collection.Add(item);
        }
        return true;
    }

    void ReindexPortPreviews()
    {
        // Preview from the configured range start, so the hint matches what create will actually allocate.
        var previewBase = Services.Settings.Get().PortRangeStart;
        for (var i = 0; i < Ports.Count; i++)
            Ports[i].Preview = (previewBase + i).ToString(CultureInfo.InvariantCulture);
    }

    void Reload()
    {
        Stacks.Clear();
        foreach (var s in Services.Stacks.List()) Stacks.Add(s);
        OnPropertyChanged(nameof(HasStacks));
        NavCount = Stacks.Count;

        SyncRepoChoices();
    }

    /// <summary>Reconcile the repo picker against the registry, preserving any current selections so a
    /// repo added (or removed) on the Repos tab shows up here without clobbering an in-progress build.</summary>
    void SyncRepoChoices()
    {
        var selected = RepoChoices.Where(c => c.IsSelected).Select(c => c.Name).ToHashSet();

        foreach (var c in RepoChoices) c.PropertyChanged -= OnChoiceChanged;
        RepoChoices.Clear();
        foreach (var r in Services.Repos.List())
        {
            var choice = new RepoChoiceViewModel(r.Name) { IsSelected = selected.Contains(r.Name) };
            choice.PropertyChanged += OnChoiceChanged;
            RepoChoices.Add(choice);
        }
        OnPropertyChanged(nameof(HasAnyRepos));
        RecomputeBindingGroups();
    }

    void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoChoiceViewModel.IsSelected))
            RecomputeBindingGroups();
    }

    /// <summary>Build a binding group per selected repo, one row per declared input (with its example hint).</summary>
    void RecomputeBindingGroups()
    {
        var desired = RepoChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();

        foreach (var group in Bindings.Where(g => !desired.Contains(g.Repo)).ToList())
        {
            foreach (var row in group.Rows) row.PropertyChanged -= OnBindingRowChanged;
            Bindings.Remove(group);
        }

        foreach (var repoName in desired)
        {
            if (Services.Repos.Get(repoName) is null) continue;
            var inputs = LoadInputs(repoName);

            var group = Bindings.FirstOrDefault(g => g.Repo == repoName);
            if (group is null) { group = new RepoBindingGroup(repoName); Bindings.Add(group); }

            foreach (var row in group.Rows.Where(r => inputs.All(i => i.Name != r.Input)).ToList())
            {
                row.PropertyChanged -= OnBindingRowChanged;
                group.Rows.Remove(row);
            }
            foreach (var input in inputs)
                if (group.Rows.All(r => r.Input != input.Name))
                {
                    var row = new BindingRow(input.Name, input.Example);
                    row.PropertyChanged += OnBindingRowChanged;
                    group.Rows.Add(row);
                }
        }

        OnPropertyChanged(nameof(CanAutoWire));
        RefreshAddableRepos();

        // Wire by convention as soon as there's something to wire, so the canvas arrives as a guess to
        // review rather than a blank board to author. Safe now that ports and bindings carry provenance:
        // AutoWire discards its own previous proposal first, so this is a fresh batch pass over the user's
        // state every time — never one that feeds itself the ports it invented a moment ago.
        //
        // Creating only. Editing must show exactly what was saved, and EditSelected picks the repos before
        // applying stored bindings, so wiring here would invent bindings for inputs the stack left unbound.
        if (CanAutoWire && !IsEditing) AutoWire();

        RebuildBuilderWiring();
    }

    /// <summary>Keep the canvas "add repo" list to the registered repos not already in the stack.</summary>
    void RefreshAddableRepos()
    {
        AddableRepos.Clear();
        foreach (var c in RepoChoices.Where(c => !c.IsSelected)) AddableRepos.Add(c.Name);
    }

    /// <summary>Add a repo to the stack by name (canvas "add repo" slot).</summary>
    [RelayCommand]
    private void AddStackRepo(string? name)
    {
        if (RepoChoices.FirstOrDefault(c => c.Name == name) is { } choice) choice.IsSelected = true;
    }

    /// <summary>Remove a repo from the stack by name (canvas trash icon, after confirm).</summary>
    [RelayCommand]
    private void RemoveStackRepo(string? name)
    {
        if (RepoChoices.FirstOrDefault(c => c.Name == name) is { } choice) choice.IsSelected = false;
    }

    /// <summary>
    /// Reorder a repo box on the canvas (drag by its header). The <see cref="Bindings"/> order is the
    /// authoritative repo order — it's what the graph draws and what save persists — so a move here
    /// carries straight through to the saved stack. <paramref name="request"/> carries graph indices,
    /// which map 1:1 to <see cref="Bindings"/>.
    /// </summary>
    [RelayCommand]
    private void ReorderRepo(ReorderRepoRequest? request)
    {
        if (request is null || request.From < 0 || request.From >= Bindings.Count) return;
        var to = Math.Clamp(request.To, 0, Bindings.Count - 1);
        if (to == request.From) return;
        Bindings.Move(request.From, to);
        RebuildBuilderWiring();
    }

    /// <summary>
    /// Reorder a port on the rail (drag by its grip). The graph's port list is the non-empty subset of
    /// <see cref="Ports"/> in order, so we move relative to the target port's real row — keeping any
    /// blank (not-yet-named) rows where they are. Previews reindex since they track position.
    /// </summary>
    [RelayCommand]
    private void ReorderPort(ReorderPortRequest? request)
    {
        if (request is null) return;
        var named = Ports.Where(p => p.Name.Trim().Length > 0).ToList();
        if (request.From < 0 || request.From >= named.Count) return;
        var to = Math.Clamp(request.To, 0, named.Count - 1);
        if (to == request.From) return;

        var vmFrom = Ports.IndexOf(named[request.From]);
        var vmTo = Ports.IndexOf(named[to]);
        if (vmFrom < 0 || vmTo < 0 || vmFrom == vmTo) return;
        Ports.Move(vmFrom, vmTo);
        ReindexPortPreviews();
        RebuildBuilderWiring();
    }

    void OnBindingRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BindingRow.Expression)) return;

        // Any hand edit (typing, or wiring from the canvas) takes the expression away from auto-wire, so a
        // later recompute leaves it alone. Suppressed while auto-wire is the one writing.
        if (!_applyingAutoWire && sender is BindingRow row) row.Auto = false;

        RebuildBuilderWiring();
    }

    /// <summary>A repo's declared inputs, or an empty list if it's gone or its config won't parse.</summary>
    List<InputDeclaration> LoadInputs(string repoName)
    {
        var reg = Services.Repos.Get(repoName);
        if (reg is null) return [];
        try { return SprigConfigLoader.LoadFromFile(Path.Combine(reg.Path, ".sprig.json")).Inputs.ToList(); }
        catch { return []; }
    }

    /// <summary>Enabled once at least one repo (hence a binding group) is in the builder.</summary>
    public bool CanAutoWire => Bindings.Count > 0;

    /// <summary>
    /// Fill the mechanical wiring: propose a port + binding for every still-unbound input, reusing
    /// existing ports by name and wrapping URL-shaped inputs as a localhost transform. Anything the
    /// user already typed is left untouched.
    /// </summary>
    [RelayCommand]
    private void AutoWire()
    {
        var repos = RepoChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        if (repos.Count == 0) return;

        _applyingAutoWire = true;
        try
        {
            // Throw away the previous proposal first, so what follows is a single batch pass over the
            // user's state alone. Without this, re-proposing feeds StackAutowire the ports it invented
            // last time; because it reuses a port whose name matches, a second repo would adopt the
            // first repo's port and two services each declaring `port` would collide at runtime.
            // Provenance is what makes re-proposing safe, and therefore automatic.
            DiscardAutoWiring();

            var autowireRepos = repos.Select(r => new AutowireRepo(r, LoadInputs(r))).ToList();

            var existingBindings = Bindings.ToDictionary(
                g => g.Repo,
                g => (IReadOnlyDictionary<string, string>)g.Rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Expression))
                    .ToDictionary(r => r.Input, r => r.Expression.Trim()));

            var existingPorts = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();

            var proposal = StackAutowire.Propose(autowireRepos, existingPorts, existingBindings);

            SetPorts(proposal.Ports);
            foreach (var group in Bindings)
                if (proposal.Bindings.TryGetValue(group.Repo, out var proposed))
                    foreach (var row in group.Rows)
                        if (proposed.TryGetValue(row.Input, out var expr) && row.Expression != expr)
                        {
                            row.Expression = expr;
                            row.Auto = true;
                        }
        }
        finally { _applyingAutoWire = false; }
    }

    /// <summary>
    /// Drop everything the last auto-wire proposed — its ports and the expressions it wrote — leaving only
    /// what the user authored. Anything the user added, renamed, typed or wired by hand survives.
    /// </summary>
    void DiscardAutoWiring()
    {
        foreach (var group in Bindings)
            foreach (var row in group.Rows)
                if (row.Auto)
                {
                    row.Expression = "";
                    row.Auto = false;
                }

        foreach (var port in Ports.Where(p => p.Auto).ToList())
        {
            port.PropertyChanged -= OnPortRowChanged;
            Ports.Remove(port);
        }
    }

    /// <summary>
    /// Replace the port rows (re-subscribing change events) and refresh previews + autosuggest. Called by
    /// auto-wire with its proposal, which includes the user's own ports — so provenance is carried across
    /// rather than reset, or a user-named port would silently become auto-wire's to delete next time.
    /// </summary>
    void SetPorts(IEnumerable<string> names)
    {
        var userNamed = Ports.Where(p => !p.Auto)
            .Select(p => p.Name.Trim())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in Ports) row.PropertyChanged -= OnPortRowChanged;
        Ports.Clear();
        foreach (var n in names) Ports.Add(NewPortRow(n, auto: !userNamed.Contains(n)));
        ReindexPortPreviews();
        RebuildBindingVariables();
        RebuildBuilderWiring();
    }
}

public partial class RepoChoiceViewModel(string name) : ViewModelBase
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}

public partial class StackPortRow : ViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _preview = "";

    /// <summary>The last committed name, so a rename can be detected and propagated to bindings.</summary>
    public string CommittedName { get; set; } = "";

    /// <summary>
    /// True when auto-wire created this port, false when the user did (added it, renamed it, or loaded it
    /// from a saved stack). Auto-wire discards its own ports before re-proposing, so it always runs against
    /// the user's state alone — see <c>StacksViewModel.AutoWire</c>.
    /// </summary>
    public bool Auto { get; set; }
}

public sealed partial class RepoBindingGroup(string repo) : ViewModelBase
{
    public string Repo { get; } = repo;
    public ObservableCollection<BindingRow> Rows { get; } = [];
}

public partial class BindingRow(string input, string? example) : ViewModelBase
{
    public string Input { get; } = input;
    public string? Example { get; } = example;

    /// <summary>The one expression that fills this input — the single source of truth the canvas edits.</summary>
    [ObservableProperty] private string _expression = "";

    /// <summary>
    /// True when auto-wire wrote this expression, false once the user touches it. Auto-wire clears its own
    /// bindings before re-proposing, so a hand-written expression is never recomputed away.
    /// </summary>
    public bool Auto { get; set; }
}

/// <summary>Read-only projection of a stack's bindings for one repo (detail panel).</summary>
public sealed record StackBindingView(string Repo, IReadOnlyList<StackBindingRowView> Rows);
public sealed record StackBindingRowView(string Input, string Expression);
