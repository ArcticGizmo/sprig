using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Config;
using Sprig.Core.Stacks;

namespace Sprig.App.ViewModels;

public partial class StacksViewModel : PageViewModel
{
    protected readonly AppServices Services;
    const int PortPreviewBase = 20000;

    public StacksViewModel(AppServices services)
    {
        Services = services;
        Reload();
    }

    public override string Title => "Stacks";

    public ObservableCollection<StackDefinition> Stacks { get; } = [];
    public ObservableCollection<RepoChoiceViewModel> RepoChoices { get; } = [];

    /// <summary>The stack's named ports (auto-allocated at create; shown with an incrementing preview).</summary>
    public ObservableCollection<StackPortRow> Ports { get; } = [];

    /// <summary>Per-repo input bindings for the selected repos.</summary>
    public ObservableCollection<RepoBindingGroup> Bindings { get; } = [];

    [ObservableProperty] private StackDefinition? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isCreating;

    /// <summary>Open the create-stack modal with a fresh, empty form.</summary>
    [RelayCommand]
    private void NewStack()
    {
        NewName = "";
        foreach (var c in RepoChoices) c.IsSelected = false;
        Ports.Clear();
        Bindings.Clear();
        Error = null; Status = null;
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate() { IsCreating = false; Error = null; }

    [RelayCommand]
    private void AddPort() { Ports.Add(new StackPortRow()); ReindexPortPreviews(); }

    [RelayCommand]
    private void RemovePort(StackPortRow row) { Ports.Remove(row); ReindexPortPreviews(); }

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

        Error = null; Status = null;
        try
        {
            Services.Stacks.Save(new StackDefinition { Name = name, Repos = repos, Ports = ports, Bindings = bindings });
            NewName = "";
            foreach (var c in RepoChoices) c.IsSelected = false;
            Ports.Clear();
            Bindings.Clear();
            IsCreating = false;
            Status = $"created stack '{name}'";
            Reload();
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        Services.Stacks.Remove(name);
        Status = $"removed stack '{name}'";
        Reload();
    }

    void ReindexPortPreviews()
    {
        for (var i = 0; i < Ports.Count; i++)
            Ports[i].Preview = (PortPreviewBase + i).ToString(CultureInfo.InvariantCulture);
    }

    void Reload()
    {
        Stacks.Clear();
        foreach (var s in Services.Stacks.List()) Stacks.Add(s);

        RepoChoices.Clear();
        foreach (var r in Services.Repos.List())
        {
            var choice = new RepoChoiceViewModel(r.Name);
            choice.PropertyChanged += OnChoiceChanged;
            RepoChoices.Add(choice);
        }
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
            Bindings.Remove(group);

        foreach (var repoName in desired)
        {
            var reg = Services.Repos.Get(repoName);
            if (reg is null) continue;

            List<InputDeclaration> inputs;
            try { inputs = SprigConfigLoader.LoadFromFile(Path.Combine(reg.Path, ".sprig.json")).Inputs.ToList(); }
            catch { inputs = []; }

            var group = Bindings.FirstOrDefault(g => g.Repo == repoName);
            if (group is null) { group = new RepoBindingGroup(repoName); Bindings.Add(group); }

            foreach (var row in group.Rows.Where(r => inputs.All(i => i.Name != r.Input)).ToList())
                group.Rows.Remove(row);
            foreach (var input in inputs)
                if (group.Rows.All(r => r.Input != input.Name))
                    group.Rows.Add(new BindingRow(input.Name, input.Example));
        }
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
}

public sealed class RepoBindingGroup(string repo) : ViewModelBase
{
    public string Repo { get; } = repo;
    public ObservableCollection<BindingRow> Rows { get; } = [];
}

public partial class BindingRow(string input, string? example) : ViewModelBase
{
    public string Input { get; } = input;
    public string? Example { get; } = example;
    [ObservableProperty] private string _expression = "";
}
