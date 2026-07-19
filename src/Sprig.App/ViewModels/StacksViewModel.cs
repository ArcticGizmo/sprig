using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty] private StackDefinition? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;

    [RelayCommand]
    private void Create()
    {
        var name = NewName.Trim();
        var repos = RepoChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        Error = null; Status = null;
        try
        {
            Services.Stacks.Save(new StackDefinition { Name = name, Repos = repos });
            NewName = "";
            foreach (var c in RepoChoices) c.IsSelected = false;
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

        var registered = Services.Repos.List().Select(r => r.Name).ToList();
        RepoChoices.Clear();
        foreach (var r in registered) RepoChoices.Add(new RepoChoiceViewModel(r));
    }
}

public partial class RepoChoiceViewModel(string name) : ViewModelBase
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}
