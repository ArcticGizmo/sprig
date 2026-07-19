using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Stacks;

namespace Sprig.App.ViewModels;

public partial class ReposViewModel : PageViewModel
{
    protected readonly AppServices Services;

    public ReposViewModel(AppServices services)
    {
        Services = services;
        Reload();
    }

    public override string Title => "Repos";

    public ObservableCollection<RegisteredRepo> Repos { get; } = [];

    [ObservableProperty] private RegisteredRepo? _selected;
    [ObservableProperty] private RepoConfigViewModel? _selectedConfig;
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;

    public bool HasSelected => Selected is not null;

    /// <summary>False when no repos are registered yet — drives the first-run empty state.</summary>
    public bool HasRepos => Repos.Count > 0;

    partial void OnSelectedChanged(RegisteredRepo? value)
    {
        SelectedConfig = value is null ? null : RepoConfigViewModel.Load(value.Path);
        OnPropertyChanged(nameof(HasSelected));
    }

    [RelayCommand]
    private Task Add() => AddInternal(runInit: false);

    [RelayCommand]
    private Task InitAndAdd() => AddInternal(runInit: true);

    async Task AddInternal(bool runInit)
    {
        var path = NewPath.Trim();
        if (string.IsNullOrEmpty(path)) { Error = "enter a repo path"; return; }

        Busy = true; Error = null; Status = null;
        try
        {
            var added = await AppServices.RunAsync(() =>
            {
                if (runInit)
                {
                    var proposal = Services.Init.Inspect(path);
                    ConfigJson.Write(proposal.Config, Path.Combine(path, ".sprig.json"));
                }
                return Services.Repos.Add(path);
            });
            NewPath = "";
            Status = $"registered '{added.Name}'";
            Reload();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;
        Services.Repos.Remove(Selected.Name);
        Status = $"unregistered '{Selected.Name}'";
        Reload();
    }

    void Reload()
    {
        Repos.Clear();
        foreach (var r in Services.Repos.List()) Repos.Add(r);
        OnPropertyChanged(nameof(HasRepos));
    }
}
