using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Config;
using Sprig.Core.Planning;
using Sprig.Core.Ports;
using Sprig.Core.Shared;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>
/// Set up → Shared: the machine-local pooled resources, and everything you arrive at this page to find
/// out — <b>is it running</b>, <b>who's holding my slots</b>, and <b>what is it changing on my machine</b>.
///
/// <para>The last of those is the price of a hidden layer: an overlay rewrites values without appearing in
/// any file you share, so the page that owns it has to be the place that explains it.</para>
/// </summary>
public partial class SharedViewModel : PageViewModel
{
    readonly AppServices _services;

    public SharedViewModel(AppServices services)
    {
        _services = services;
        Reload();
    }

    public override string Title => "Shared";

    public ObservableCollection<SharedResourceItem> Resources { get; } = [];

    [ObservableProperty] private SharedResourceItem? _selected;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;

    public bool HasResources => Resources.Count > 0;
    public bool HasSelected => Selected is not null;

    // ---- delete confirmation -------------------------------------------------------------------

    /// <summary>True while the typed-name delete confirmation is showing.</summary>
    [ObservableProperty] private bool _confirmingDelete;

    /// <summary>What the user has typed to confirm a delete. Must match the resource name exactly.</summary>
    [ObservableProperty] private string _deleteConfirmText = "";

    public bool CanConfirmDelete =>
        Selected is not null && string.Equals(DeleteConfirmText, Selected.Name, StringComparison.Ordinal);

    partial void OnDeleteConfirmTextChanged(string value) => OnPropertyChanged(nameof(CanConfirmDelete));

    // ---- capacity ------------------------------------------------------------------------------

    /// <summary>True while the capacity editor is showing.</summary>
    [ObservableProperty] private bool _editingCapacity;
    [ObservableProperty] private string _capacityText = "";

    // ---- extraction ----------------------------------------------------------------------------

    /// <summary>True while the "New shared resource" flow is open.</summary>
    [ObservableProperty] private bool _isExtracting;

    public ObservableCollection<ExtractRepoOption> ExtractRepos { get; } = [];
    public ObservableCollection<string> ExtractFiles { get; } = [];
    public ObservableCollection<SharedResourceExtractor.ComposeService> ExtractServices { get; } = [];

    [ObservableProperty] private ExtractRepoOption? _extractRepo;
    [ObservableProperty] private string? _extractFile;
    [ObservableProperty] private SharedResourceExtractor.ComposeService? _extractService;
    [ObservableProperty] private string _extractName = "";
    [ObservableProperty] private string _extractCapacity = "5";
    [ObservableProperty] private string? _extractError;

    /// <summary>The proposal being previewed. Nothing is written until it's accepted.</summary>
    [ObservableProperty] private ExtractPreview? _preview;

    public bool CanPreview => ExtractRepo is not null && ExtractFile is not null
                              && ExtractService is { Poolable: true };

    partial void OnExtractRepoChanged(ExtractRepoOption? value)
    {
        Preview = null;
        ExtractFiles.Clear();
        ExtractServices.Clear();
        ExtractFile = null;
        if (value is null) { OnPropertyChanged(nameof(CanPreview)); return; }

        foreach (var file in value.ComposeFiles) ExtractFiles.Add(file);
        ExtractFile = ExtractFiles.FirstOrDefault();
        OnPropertyChanged(nameof(CanPreview));
    }

    partial void OnExtractFileChanged(string? value)
    {
        Preview = null;
        ExtractServices.Clear();
        ExtractService = null;
        if (ExtractRepo is null || value is null) { OnPropertyChanged(nameof(CanPreview)); return; }

        foreach (var service in SharedResourceExtractor.Services(ExtractRepo.Root, value))
            ExtractServices.Add(service);
        ExtractService = ExtractServices.FirstOrDefault(s => s is { Poolable: true, HasPreset: true })
                         ?? ExtractServices.FirstOrDefault(s => s.Poolable);
        OnPropertyChanged(nameof(CanPreview));
    }

    partial void OnExtractServiceChanged(SharedResourceExtractor.ComposeService? value)
    {
        Preview = null;
        ExtractName = value?.Image is { Length: > 0 } image
            ? SharedResourcePreset.NameFor(image)
            : value?.Name ?? "";
        OnPropertyChanged(nameof(CanPreview));
    }

    // ---- loading -------------------------------------------------------------------------------

    protected override void OnActivated() => Reload();

    /// <summary>Rebuild the list, keeping the current selection if it survives.</summary>
    public void Reload()
    {
        var keep = Selected?.Name;
        Resources.Clear();

        foreach (var resource in _services.Shared.Resources.List())
            Resources.Add(Build(resource));

        Selected = Resources.FirstOrDefault(r => r.Name == keep) ?? Resources.FirstOrDefault();
        NavCount = Resources.Count;
        OnPropertyChanged(nameof(HasResources));
    }

    SharedResourceItem Build(SharedResourceDefinition resource)
    {
        var slots = _services.Shared.Leases.List(resource.Name);
        var records = _services.Workspaces.List().ToDictionary(i => i.Workspace, StringComparer.Ordinal);

        var rows = slots
            .Select(slot => new SlotRow(
                slot.Slot,
                slot.Workspace,
                string.Join(", ", slot.Namespaces.Select(n => n.Label)),
                records.TryGetValue(slot.Workspace, out var record) ? record.LastStatus ?? "created" : "missing",
                Age(slot.AttachedAt),
                (DateTimeOffset.UtcNow - slot.AttachedAt).TotalDays >= StaleAfterDays))
            .ToList();

        // The one thing a hidden layer owes you: what it is changing, and where.
        var changes = resource.Injects.SelectMany(Changes).ToList();

        // Which stacks it reaches, and how many live workspaces sit on it. Applies-to is by repo, so a
        // stack qualifies simply by containing one.
        var repos = resource.Injects.Select(i => i.Repo).ToHashSet(StringComparer.Ordinal);
        var reaches = _services.Stacks.List()
            .Where(stack => stack.Repos.Any(repos.Contains))
            .Select(stack => new ReachRow(
                stack.Name,
                string.Join(", ", stack.Repos.Where(repos.Contains)),
                _services.Workspaces.List().Count(i => i.Stack == stack.Name)))
            .ToList();

        return new SharedResourceItem(resource, rows, changes, reaches, Running(resource));
    }

    bool Running(SharedResourceDefinition resource)
    {
        // Never let a stopped Docker Desktop make the page unusable — an unknown state reads as stopped.
        try { return _services.Shared.Runner.IsRunning(resource); }
        catch (Exception) { return false; }
    }

    const int StaleAfterDays = 7;

    static IEnumerable<ChangeRow> Changes(ResourceInjection inject)
    {
        foreach (var (input, expression) in inject.Inputs)
            yield return new ChangeRow(PlanLayer.Stack, inject.Repo, PlanTargets.Input(input), expression);

        foreach (var env in inject.Env)
            foreach (var (key, template) in env.Set)
                yield return new ChangeRow(PlanLayer.Repo, inject.Repo,
                    PlanTargets.EnvKey(env.File, key), template);

        foreach (var compose in inject.Compose)
            foreach (var over in compose.Overrides)
                yield return new ChangeRow(PlanLayer.Repo, inject.Repo,
                    PlanTargets.ComposePath(compose.File, over.Path), over.Template);

        foreach (var suppress in inject.Suppress)
            foreach (var service in suppress.Services)
                yield return new ChangeRow(PlanLayer.Repo, inject.Repo,
                    PlanTargets.ComposeService(suppress.File, service), "not started — provided here");
    }

    static string Age(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h"
            : $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    partial void OnSelectedChanged(SharedResourceItem? value)
    {
        ConfirmingDelete = false;
        DeleteConfirmText = "";
        EditingCapacity = false;
        CapacityText = value?.Definition.Capacity.ToString(CultureInfo.InvariantCulture) ?? "5";
        Error = null;
        Status = null;
        OnPropertyChanged(nameof(HasSelected));
        OnPropertyChanged(nameof(CanConfirmDelete));
    }

    // ---- lifecycle actions ---------------------------------------------------------------------

    [RelayCommand]
    private async Task StartAsync()
    {
        if (Selected is not { } item) return;
        await RunAsync($"{item.Name} started", () => _services.Shared.Runner.EnsureUp(item.Definition));
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Selected is not { } item) return;
        // Deliberate, explicit stop: this button means "stop it", so it doesn't consult the refcount the
        // way `sprig down` does. Attached workspaces keep their data; they just have to start it again.
        await RunAsync($"{item.Name} stopped",
            () => _services.Shared.Runner.StopIfIdle(item.Definition with { WhenIdle = "stop" }, otherUsers: false));
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (Selected is not { } item) return;
        var enabling = !item.Definition.Enabled;
        await RunAsync(enabling ? $"{item.Name} enabled" : $"{item.Name} disabled — new workspaces won't use it",
            () => _services.Shared.Resources.Save(item.Definition with { Enabled = enabling }));
    }

    [RelayCommand]
    private void BeginEditCapacity()
    {
        if (Selected is null) return;
        CapacityText = Selected.Definition.Capacity.ToString(CultureInfo.InvariantCulture);
        EditingCapacity = true;
    }

    [RelayCommand]
    private void CancelEditCapacity() => EditingCapacity = false;

    [RelayCommand]
    private async Task SaveCapacityAsync()
    {
        if (Selected is not { } item) return;
        if (!int.TryParse(CapacityText, out var capacity) || capacity < 1)
        {
            Error = "Capacity must be a whole number of workspaces, at least 1.";
            return;
        }
        if (capacity < item.Slots.Count)
        {
            Error = $"{item.Slots.Count} workspaces are already attached — capacity can't go below that. " +
                    "Remove one first.";
            return;
        }

        EditingCapacity = false;
        await RunAsync($"capacity is now {capacity}",
            () => _services.Shared.Resources.Save(item.Definition with { Capacity = capacity }));
    }

    /// <summary>Free slots held by workspaces that no longer exist — the fix for a pool that reads as full.</summary>
    [RelayCommand]
    private async Task ReclaimAsync()
    {
        var known = _services.Workspaces.List().Select(i => i.Workspace).ToList();
        var dropped = await AppServices.RunAsync(() => _services.Shared.Leases.Reclaim(known));

        Status = dropped.Count == 0
            ? "No stale slots — every slot belongs to a workspace that still exists."
            : $"Reclaimed {dropped.Count} slot{(dropped.Count == 1 ? "" : "s")} from " +
              string.Join(", ", dropped.Select(d => d.Workspace));
        Reload();
    }

    [RelayCommand]
    private void BeginDelete()
    {
        DeleteConfirmText = "";
        ConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        ConfirmingDelete = false;
        DeleteConfirmText = "";
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (Selected is not { } item || !CanConfirmDelete) return;

        ConfirmingDelete = false;
        var name = item.Name;
        var definition = item.Definition;
        await RunAsync($"deleted '{name}'", () =>
        {
            // The confirmation promised the container and volume go with it, so they do — in this order,
            // because a half-deleted resource with live leases is worse than either state on its own.
            _services.Shared.Runner.Destroy(definition);
            foreach (var slot in _services.Shared.Leases.List(name))
                _services.Shared.Leases.Release(name, slot.Workspace);

            _services.Shared.Resources.Remove(name);
            // The host port was leased under a pseudo-workspace; give it back with the resource.
            _services.Ports.Release($"@shared/{name}");
        });
    }

    // ---- extraction ----------------------------------------------------------------------------

    [RelayCommand]
    private void BeginExtract()
    {
        ExtractError = null;
        Preview = null;
        ExtractRepos.Clear();

        foreach (var repo in _services.Repos.List())
        {
            var option = ExtractRepoOption.TryLoad(repo.Name, repo.Path);
            if (option is not null) ExtractRepos.Add(option);
        }

        ExtractRepo = ExtractRepos.FirstOrDefault();
        ExtractCapacity = "5";
        IsExtracting = true;
    }

    [RelayCommand]
    private void CancelExtract()
    {
        IsExtracting = false;
        Preview = null;
        ExtractError = null;
    }

    /// <summary>Build the proposal. Reserves nothing — the host port is leased only on accept.</summary>
    [RelayCommand]
    private void BuildPreview()
    {
        if (!CanPreview) return;
        ExtractError = null;
        try
        {
            var capacity = int.TryParse(ExtractCapacity, out var parsed) && parsed > 0 ? parsed : 5;
            var proposal = SharedResourceExtractor.Propose(
                ExtractRepo!.Config, ExtractRepo.Root, ExtractFile!, ExtractService!.Name,
                string.IsNullOrWhiteSpace(ExtractName) ? null : ExtractName.Trim(), capacity);
            Preview = new ExtractPreview(proposal);
        }
        catch (Exception ex)
        {
            Preview = null;
            ExtractError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AcceptExtractAsync()
    {
        if (Preview is null || ExtractRepo is null) return;
        ExtractError = null;
        Busy = true;
        try
        {
            var name = Preview.Proposal.Resource.Name;
            if (_services.Shared.Resources.Get(name) is not null)
            {
                ExtractError = $"A shared resource called '{name}' already exists. Give this one another name.";
                return;
            }

            var capacity = int.TryParse(ExtractCapacity, out var parsed) && parsed > 0 ? parsed : 5;
            var repo = ExtractRepo;
            var file = ExtractFile!;
            var service = ExtractService!.Name;
            var chosen = string.IsNullOrWhiteSpace(ExtractName) ? null : ExtractName.Trim();

            await AppServices.RunAsync(() =>
            {
                // One address for everybody, from the same ledger every other port comes from — the
                // service's conventional number is often already taken by something sprig can't see.
                var leased = _services.Ports.Acquire($"@shared/{name}", [new PortRequest("port")]);
                var final = SharedResourceExtractor.Propose(
                    repo.Config, repo.Root, file, service, chosen, capacity, leased["port"]);

                Directory.CreateDirectory(_services.Paths.SharedDir);
                File.WriteAllText(
                    Path.Combine(_services.Paths.SharedDir, final.ComposeFragmentFileName),
                    final.ComposeFragment);
                _services.Shared.Resources.Save(final.Resource);
            });

            IsExtracting = false;
            Preview = null;
            Status = $"Created '{name}'. New workspaces on stacks using {repo.Name} will pool it.";
            Reload();
            Selected = Resources.FirstOrDefault(r => r.Name == name) ?? Selected;
            _services.NotifyStoreChanged();
        }
        catch (Exception ex)
        {
            ExtractError = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ---- plumbing ------------------------------------------------------------------------------

    async Task RunAsync(string success, Action work)
    {
        Busy = true;
        Error = null;
        Status = null;
        try
        {
            await AppServices.RunAsync(work);
            Status = success;
            Reload();
            _services.NotifyStoreChanged();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>A repo the extract flow can pull a service out of — one that declares compose files.</summary>
public sealed record ExtractRepoOption(string Name, string Root, SprigRepoConfig Config)
{
    public IReadOnlyList<string> ComposeFiles => [.. Config.Compose.Select(c => c.File)];

    public override string ToString() => Name;

    /// <summary>Load a registered repo's config; null when it has no compose files to extract from.</summary>
    public static ExtractRepoOption? TryLoad(string name, string path)
    {
        try
        {
            var config = SprigConfigLoader.LoadFromFile(
                Path.Combine(path, WorkspaceService.ConfigFileName));
            return config.Compose.Count == 0 ? null : new ExtractRepoOption(name, path, config);
        }
        catch (Exception)
        {
            return null;   // an unreadable repo simply isn't offered
        }
    }
}

/// <summary>One shared resource as the page shows it: the definition plus everything derived around it.</summary>
public sealed class SharedResourceItem(
    SharedResourceDefinition definition,
    IReadOnlyList<SlotRow> slots,
    IReadOnlyList<ChangeRow> changes,
    IReadOnlyList<ReachRow> reaches,
    bool isRunning)
{
    public SharedResourceDefinition Definition { get; } = definition;
    public IReadOnlyList<SlotRow> Slots { get; } = slots;
    public IReadOnlyList<ChangeRow> Changes { get; } = changes;
    public IReadOnlyList<ReachRow> Reaches { get; } = reaches;

    public string Name => Definition.Name;
    public bool IsEnabled => Definition.Enabled;
    public bool IsRunning { get; } = isRunning;
    public string RunState => IsRunning ? "running" : "stopped";
    public string EnabledLabel => IsEnabled ? "Disable" : "Enable";
    public bool IsDisabled => !IsEnabled;

    public int Capacity => Definition.Capacity;
    public int Attached => Slots.Count;
    public string CapacityLabel => $"{Attached} / {Capacity} attached";
    public bool AtCapacity => Attached >= Capacity;
    public string Image => Definition.Values.TryGetValue("image", out var image) ? image : Definition.Name;

    /// <summary>Where repos actually connect. The single fact the whole feature turns on.</summary>
    public string Address => Definition.Values.TryGetValue("port", out var port)
        ? $"{Definition.Values.GetValueOrDefault("host", "localhost")}:{port}"
        : "—";

    public string WhenIdleLabel => Definition.WhenIdle == "keep"
        ? "kept running when idle"
        : "stopped when nothing is using it";

    /// <summary>Slots held by workspaces nobody has run in a while — the usual reason a pool reads as full.</summary>
    public int StaleCount => Slots.Count(s => s.IsStale);
    public bool HasStale => StaleCount > 0;

    /// <summary>The nudge shown when the pool is full: say what will fail, and why it probably shouldn't.</summary>
    public string CapacityWarning => HasStale
        ? $"The next workspace on a stack using {string.Join(", ", Reaches.Select(r => r.Stack).DefaultIfEmpty("this repo"))} " +
          $"will fail to create. {StaleCount} of {Attached} slots belong to workspaces that haven't run in over a week."
        : $"The next workspace on a stack using this resource will fail to create. Raise the capacity, or " +
          "remove a workspace to free a slot.";

    /// <summary>Editing what a live workspace was built on would mislead, so it's blocked while slots are held.</summary>
    public bool IsLocked => Attached > 0;
    public string LockNote => IsLocked
        ? $"{Attached} workspace{(Attached == 1 ? " is" : "s are")} attached — compose, values and injection " +
          "points are locked. Capacity can still be raised."
        : "";

    public bool HasSlots => Slots.Count > 0;
    public bool HasReaches => Reaches.Count > 0;
}

/// <summary>One slot on a shared resource.</summary>
public sealed record SlotRow(int Slot, string Workspace, string Namespace, string WorkspaceStatus,
    string Age, bool IsStale)
{
    public string SlotLabel => $"slot {Slot}";
    public string Detail => $"{WorkspaceStatus} · attached {Age} ago";
}

/// <summary>One override the resource makes, and which layer it lands at.</summary>
public sealed record ChangeRow(PlanLayer Layer, string Repo, string Target, string Value)
{
    public string LayerLabel => Layer switch
    {
        PlanLayer.Repo => "repo",
        PlanLayer.Stack => "stack",
        _ => "shared",
    };

    /// <summary>Teal marks the values this layer took over; the base layers keep their own colours.</summary>
    public bool IsStackLayer => Layer == PlanLayer.Stack;
}

/// <summary>A stack this resource reaches, because it contains one of the repos the resource injects.</summary>
public sealed record ReachRow(string Stack, string ViaRepos, int Workspaces)
{
    public string WorkspacesLabel => $"{Workspaces} workspace{(Workspaces == 1 ? "" : "s")}";
}

/// <summary>An extraction proposal, flattened for display. Nothing here has been written.</summary>
public sealed class ExtractPreview(ExtractionProposal proposal)
{
    public ExtractionProposal Proposal { get; } = proposal;

    public string Name => Proposal.Resource.Name;
    public string ExecService => Proposal.Resource.ExecService ?? "—";
    public int Capacity => Proposal.Resource.Capacity;

    public IReadOnlyList<PreviewValue> Values =>
        [.. Proposal.Resource.Values.OrderBy(v => v.Key, StringComparer.Ordinal)
              .Select(v => new PreviewValue(v.Key, v.Value))];

    public IReadOnlyList<ExtractionChoice> Choices => Proposal.Choices;
    public IReadOnlyList<string> Warnings => Proposal.Warnings;
    public bool HasWarnings => Warnings.Count > 0;

    public string Fragment => Proposal.ComposeFragment;
}

/// <summary>One published value in the extraction preview.</summary>
public sealed record PreviewValue(string Key, string Template)
{
    /// <summary>
    /// What to show. A preview leases nothing, so the port here is only the preset's conventional number —
    /// printing it as though it were the address would be a small lie the reader can't detect.
    /// </summary>
    public string Display => Key == "port" ? "allocated when you accept" : Template;
}
