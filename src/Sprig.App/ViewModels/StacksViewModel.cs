using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    public StacksViewModel(AppServices services)
    {
        Services = services;
        Reload();
    }

    public override string Title => "Stacks";

    public ObservableCollection<StackDefinition> Stacks { get; } = [];
    public ObservableCollection<RepoChoiceViewModel> RepoChoices { get; } = [];

    /// <summary>The stack-level variables being authored (name → template/literal).</summary>
    public ObservableCollection<StackVarRow> Vars { get; } = [];

    [ObservableProperty] private StackDefinition? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;

    [RelayCommand]
    private void AddVar() => Vars.Add(new StackVarRow { IsAuto = false });

    [RelayCommand]
    private void RemoveVar(StackVarRow row) => Vars.Remove(row);

    [RelayCommand]
    private void Create()
    {
        var name = NewName.Trim();
        var repos = RepoChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        var vars = Vars
            .Where(v => !string.IsNullOrWhiteSpace(v.Key) && !string.IsNullOrWhiteSpace(v.Value))
            .ToDictionary(v => v.Key.Trim(), v => v.Value.Trim());

        Error = null; Status = null;
        try
        {
            Services.Stacks.Save(new StackDefinition { Name = name, Repos = repos, Vars = vars });
            NewName = "";
            foreach (var c in RepoChoices) c.IsSelected = false;
            Vars.Clear();
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
        RecomputeRequiredVars();
    }

    void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoChoiceViewModel.IsSelected))
            RecomputeRequiredVars();
    }

    /// <summary>Detect which stack vars the checked repos need and reflect them in the editor.</summary>
    void RecomputeRequiredVars()
    {
        var required = new List<string>();
        foreach (var choice in RepoChoices.Where(c => c.IsSelected))
        {
            var reg = Services.Repos.Get(choice.Name);
            if (reg is null) continue;
            try
            {
                var cfg = SprigConfigLoader.LoadFromFile(Path.Combine(reg.Path, ".sprig.json"));
                required.AddRange(ConfigReferences.RequiredStackVars(cfg));
            }
            catch { /* a bad config just contributes no vars */ }
        }
        var need = required.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        // Drop auto rows no longer needed and still empty; keep user rows and filled rows.
        foreach (var row in Vars.Where(v => v.IsAuto && !need.Contains(v.Key) && string.IsNullOrEmpty(v.Value)).ToList())
            Vars.Remove(row);

        // Add any newly-needed vars as empty auto rows.
        foreach (var name in need)
            if (!Vars.Any(v => v.Key == name))
                Vars.Add(new StackVarRow { Key = name, IsAuto = true });

        foreach (var row in Vars)
            row.Required = need.Contains(row.Key);
    }
}

public partial class RepoChoiceViewModel(string name) : ViewModelBase
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}

public partial class StackVarRow : ViewModelBase
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _required;

    /// <summary>True if sprig added this row from detection (vs. the user adding it manually).</summary>
    public bool IsAuto { get; init; }
}
