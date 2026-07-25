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

    [ObservableProperty] private StackDefinition? _selected;

    /// <summary>The stack name — path-compatible, used as the filename/key, worktree folder and branch.</summary>
    [ObservableProperty] private string _newName = "";
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

    /// <summary>The selected stack's per-repo bindings, flattened for the detail panel.</summary>
    public ObservableCollection<StackBindingView> DetailBindings { get; } = [];

    /// <summary>When on, the detail panel shows the selected stack as a wiring diagram instead of lists.</summary>
    [ObservableProperty] private bool _showDiagram;

    /// <summary>The selected stack's wiring, laid out by the patchbay canvas.</summary>
    [ObservableProperty] private WiringGraph? _wiring;

    public string DiagramToggleLabel => ShowDiagram ? "List" : "Diagram";

    /// <summary>Collapse the stack-list column while the diagram is up, so the patchbay gets the full width.</summary>
    public Avalonia.Controls.GridLength ListColumnWidth => ShowDiagram
        ? new Avalonia.Controls.GridLength(0)
        : new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);

    partial void OnShowDiagramChanged(bool value)
    {
        OnPropertyChanged(nameof(DiagramToggleLabel));
        OnPropertyChanged(nameof(ListColumnWidth));
    }

    [RelayCommand]
    private void ToggleDiagram() => ShowDiagram = !ShowDiagram;

    /// <summary>Derive the wiring graph for the selected stack (repos, ports, declared inputs, bindings).</summary>
    void RebuildWiring()
    {
        if (Selected is not { } stack) { Wiring = null; return; }
        var inputs = stack.Repos.ToDictionary(
            r => r,
            r => (IReadOnlyList<string>)LoadInputs(r).Select(i => i.Name).ToList());
        Wiring = WiringGraph.Build(stack.Repos, stack.Ports, inputs, stack.Bindings);
    }

    // --- The builder's own live wiring (an editable second view of the form) ----------------

    /// <summary>When on, the builder shows the editable patchbay instead of the form fields.</summary>
    [ObservableProperty] private bool _builderDiagram;

    /// <summary>The wiring graph for the in-progress build — rebuilt on every binding/port change.</summary>
    [ObservableProperty] private WiringGraph? _builderWiring;

    // The canvas is the primary builder surface; the form is the "Advanced" escape hatch behind it.
    public string BuilderViewLabel => BuilderDiagram ? "⚙ Advanced (form)" : "◨ Canvas";

    /// <summary>The canvas needs room to breathe, so keep the modal wide while it's showing.</summary>
    public double BuilderWidth => BuilderDiagram ? 1040 : 720;

    partial void OnBuilderDiagramChanged(bool value)
    {
        OnPropertyChanged(nameof(BuilderViewLabel));
        OnPropertyChanged(nameof(BuilderWidth));
    }

    [RelayCommand]
    private void ToggleBuilderDiagram() => BuilderDiagram = !BuilderDiagram;

    /// <summary>Rebuild the live graph the builder's canvas draws from the current rows + ports.</summary>
    void RebuildBuilderWiring()
    {
        var repos = Bindings.Select(g => g.Repo).ToList();
        var ports = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();
        var inputs = Bindings.ToDictionary(
            g => g.Repo, g => (IReadOnlyList<string>)g.Rows.Select(r => r.Input).ToList());
        var bindings = Bindings.ToDictionary(
            g => g.Repo, g => (IReadOnlyDictionary<string, string>)g.Rows.ToDictionary(r => r.Input, r => r.Expression));
        BuilderWiring = WiringGraph.Build(repos, ports, inputs, bindings);
    }

    BindingRow? FindRow(string repo, string input) =>
        Bindings.FirstOrDefault(g => g.Repo == repo)?.Rows.FirstOrDefault(r => r.Input == input);

    /// <summary>Bind an input to a port (drag pin → port). Reuses the row's port setter so the form agrees.</summary>
    [RelayCommand]
    private void WirePin(WireRequest? request)
    {
        if (request is null) return;
        if (FindRow(request.Repo, request.Input) is { } row) row.Port = request.Port;
    }

    /// <summary>Clear an input's binding (drag pin → empty space).</summary>
    [RelayCommand]
    private void UnwirePin(PinRef? pin)
    {
        if (pin is null) return;
        if (FindRow(pin.Repo, pin.Input) is { } row) row.Expression = "";
    }

    /// <summary>Re-shape a bound input with a transform preset (canvas pin menu).</summary>
    [RelayCommand]
    private void SetPinTransform(TransformRequest? request)
    {
        if (request is null) return;
        if (FindRow(request.Repo, request.Input) is { } row) row.SelectedTransform = request.Preset;
    }

    /// <summary>Bind an input to the workspace source (drag the workspace chip → input). Replaces any current value.</summary>
    [RelayCommand]
    private void WireWorkspace(PinRef? pin)
    {
        if (pin is null) return;
        if (FindRow(pin.Repo, pin.Input) is { } row) row.Expression = "${sprig.workspace}";
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

        if (FindRow(request.Repo, request.Input) is { } row) row.Port = name;
        RefreshClassification();
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
        RebuildWiring();
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

        Error = null; Status = null;
        BuilderDiagram = true;   // land on the canvas; the form is the Advanced view
        EditingOriginalName = stack.Name;
        NewName = stack.Name;

        foreach (var row in Ports) row.PropertyChanged -= OnPortRowChanged;
        Ports.Clear();
        foreach (var p in stack.Ports) Ports.Add(NewPortRow(p));
        ReindexPortPreviews();
        RebuildBindingVariables();

        // Reset then check the stack's repos, so RecomputeBindingGroups rebuilds a clean set of rows.
        foreach (var c in RepoChoices) c.IsSelected = false;
        foreach (var c in RepoChoices) c.IsSelected = stack.Repos.Contains(c.Name);

        foreach (var group in Bindings)
            if (stack.Bindings.TryGetValue(group.Repo, out var repoBindings))
                foreach (var row in group.Rows)
                    if (repoBindings.TryGetValue(row.Input, out var expr))
                        row.Expression = expr;

        IsCreating = true;
    }

    /// <summary>Open the builder with a fresh, empty form for a new stack.</summary>
    [RelayCommand]
    private void NewStack()
    {
        EditingOriginalName = null;
        BuilderDiagram = true;   // name-first, then straight onto the canvas
        NewName = "";
        foreach (var c in RepoChoices) c.IsSelected = false;
        Ports.Clear();
        Bindings.Clear();
        RebuildBindingVariables();
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
        RefreshClassification();
    }

    [RelayCommand]
    private void RemovePort(StackPortRow row)
    {
        row.PropertyChanged -= OnPortRowChanged;
        Ports.Remove(row);
        ReindexPortPreviews();
        RebuildBindingVariables();
        RefreshClassification();
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
                PropagatePortRename(previous, renamed);
            row.CommittedName = renamed;
        }

        RebuildBindingVariables();
        RefreshClassification();
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
    }

    /// <summary>Create a port row wired for change tracking (rename propagation + previews).</summary>
    StackPortRow NewPortRow(string name = "")
    {
        var row = new StackPortRow { Name = name, CommittedName = name };
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
        var repos = RepoChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        var ports = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();
        var bindings = Bindings.ToDictionary(
            g => g.Repo,
            g => (IReadOnlyDictionary<string, string>)g.Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Expression))
                .ToDictionary(r => r.Input, r => r.Expression.Trim()));

        var shares = StackShares.Derive(repos, ports, bindings);

        Error = null; Status = null;
        try
        {
            Services.Stacks.Save(new StackDefinition { Name = name, Repos = repos, Ports = ports, Bindings = bindings, Shares = shares });

            // Editing with a changed name: the save wrote the new file, so drop the old one.
            var edited = EditingOriginalName;
            if (edited is { } orig && orig != name) Services.Stacks.Remove(orig);

            NewName = "";
            foreach (var c in RepoChoices) c.IsSelected = false;
            Ports.Clear();
            Bindings.Clear();
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
        RefreshClassification();
    }

    void OnBindingRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BindingRow.Expression)) RefreshClassification();
    }

    /// <summary>Show or hide a group's folded identity rows.</summary>
    [RelayCommand]
    private void ToggleGroup(RepoBindingGroup group) => group.Expanded = !group.Expanded;

    /// <summary>
    /// Re-classify every binding row so the mechanical identity mappings can fold away and the
    /// exceptions (transforms, shared ports, literals, and anything unbound) stay in view with a tag.
    /// </summary>
    void RefreshClassification()
    {
        var declared = Ports.Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToList();
        var bindings = Bindings.ToDictionary(
            g => g.Repo,
            g => (IReadOnlyDictionary<string, string>)g.Rows.ToDictionary(r => r.Input, r => r.Expression));
        var all = BindingClassifier.ClassifyAll(bindings, declared);

        foreach (var group in Bindings)
        {
            var collapsible = 0;
            foreach (var row in group.Rows)
            {
                var cls = all.GetValueOrDefault((group.Repo, row.Input))
                          ?? new BindingClass(BindingKind.Unbound, false, false);
                ApplyTag(row, cls);
                row.Collapsible = cls.IsCollapsible;
                row.IsCollapsed = cls.IsCollapsible && !group.Expanded;
                if (cls.IsCollapsible) collapsible++;

                var (preset, port) = TransformPresets.Recognize(row.Expression);
                row.SyncTransform(port, preset);
            }
            group.CollapsibleCount = collapsible;
        }

        RebuildBuilderWiring();
    }

    /// <summary>Light exactly one tag chip per row, most decision-worthy first.</summary>
    static void ApplyTag(BindingRow row, BindingClass cls)
    {
        row.ShowNeedsValue = cls.Kind == BindingKind.Unbound;
        row.ShowUnknownPort = cls is { Kind: not BindingKind.Unbound, ReferencesUndeclaredPort: true };
        row.ShowShared = cls is { Shared: true, ReferencesUndeclaredPort: false } && !row.ShowNeedsValue;
        row.ShowTransform = cls is { Kind: BindingKind.Transform, Shared: false, ReferencesUndeclaredPort: false };
        row.ShowLiteral = cls.Kind == BindingKind.Literal;
        row.ShowAuto = cls.IsCollapsible;
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
                    if (proposed.TryGetValue(row.Input, out var expr))
                        row.Expression = expr;
    }

    /// <summary>Replace the port rows (re-subscribing change events) and refresh previews + autosuggest.</summary>
    void SetPorts(IEnumerable<string> names)
    {
        foreach (var row in Ports) row.PropertyChanged -= OnPortRowChanged;
        Ports.Clear();
        foreach (var n in names) Ports.Add(NewPortRow(n));
        ReindexPortPreviews();
        RebuildBindingVariables();
        RefreshClassification();
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
}

public sealed partial class RepoBindingGroup(string repo) : ViewModelBase
{
    public string Repo { get; } = repo;
    public ObservableCollection<BindingRow> Rows { get; } = [];

    /// <summary>Whether the folded identity rows are currently shown.</summary>
    [ObservableProperty] private bool _expanded;

    /// <summary>How many rows in this group are plain identity mappings that can be folded away.</summary>
    [ObservableProperty] private int _collapsibleCount;

    public bool HasCollapsible => CollapsibleCount > 0;
    public string CollapsibleSummary =>
        $"{CollapsibleCount} input{(CollapsibleCount == 1 ? "" : "s")} auto-wired to matching ports — all valid";
    public string ToggleLabel => Expanded ? "▾ hide" : "▸ review";

    partial void OnCollapsibleCountChanged(int value) => OnPropertyChanged(nameof(HasCollapsible));
    partial void OnExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleLabel));
        foreach (var row in Rows) row.IsCollapsed = row.Collapsible && !value;
    }
}

public partial class BindingRow(string input, string? example) : ViewModelBase
{
    public string Input { get; } = input;
    public string? Example { get; } = example;
    [ObservableProperty] private string _expression = "";

    /// <summary>A plain identity mapping that can be folded away (set by the classifier pass).</summary>
    [ObservableProperty] private bool _collapsible;
    /// <summary>Currently folded behind its group's summary strip.</summary>
    [ObservableProperty] private bool _isCollapsed;

    // One tag chip shows at a time; the builder's classifier sets exactly one of these.
    [ObservableProperty] private bool _showNeedsValue;
    [ObservableProperty] private bool _showUnknownPort;
    [ObservableProperty] private bool _showShared;
    [ObservableProperty] private bool _showTransform;
    [ObservableProperty] private bool _showLiteral;
    [ObservableProperty] private bool _showAuto;

    // Transform module: pick how a single port becomes this input's value. The raw token box below
    // stays the source of truth — picking a preset just rewrites it, and it re-syncs from the text.
    bool _syncingTransform;

    /// <summary>The one stack port this row references, if exactly one — the target of a transform.</summary>
    [ObservableProperty] private string? _port;
    /// <summary>The transform picker is meaningful only when the row references exactly one port.</summary>
    [ObservableProperty] private bool _canTransform;
    [ObservableProperty] private TransformPreset? _selectedTransform;

    public IReadOnlyList<TransformPreset> TransformOptions => TransformPresets.All;

    partial void OnSelectedTransformChanged(TransformPreset? value)
    {
        if (_syncingTransform || value is null || Port is null || value == TransformPresets.Custom) return;
        Expression = TransformPresets.Generate(value, Port);
    }

    /// <summary>
    /// Picking a port binds this input to it (keeping the current transform form, defaulting to raw).
    /// Choosing a port another input already uses is exactly how you share it.
    /// </summary>
    partial void OnPortChanged(string? value)
    {
        if (_syncingTransform || string.IsNullOrEmpty(value)) return;
        var preset = SelectedTransform is { } p && p != TransformPresets.Custom ? p : TransformPresets.Raw;
        Expression = TransformPresets.Generate(preset, value);
    }

    /// <summary>Reflect the current expression in the picker without treating it as a user edit.</summary>
    public void SyncTransform(string? port, TransformPreset preset)
    {
        _syncingTransform = true;
        Port = port;
        CanTransform = port is not null;
        SelectedTransform = preset;
        _syncingTransform = false;
    }
}

/// <summary>Read-only projection of a stack's bindings for one repo (detail panel).</summary>
public sealed record StackBindingView(string Repo, IReadOnlyList<StackBindingRowView> Rows);
public sealed record StackBindingRowView(string Input, string Expression);
