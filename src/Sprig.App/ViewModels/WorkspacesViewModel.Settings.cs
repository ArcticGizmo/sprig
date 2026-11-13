using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>One setting, with a box for the workspace's own value.</summary>
/// <param name="Repos">The repos that have it, for the line under its name.</param>
/// <param name="Otherwise">What it gets when the box is empty, in words; null when nothing gives it a value.</param>
/// <param name="Kept">The value the workspace has now, if any.</param>
public sealed partial class SettingInput(string name, string type, string repos, string? otherwise, string? kept) : ObservableObject
{
    public string Name => name;
    public string Type => type;
    public string Detail => $"{type} · {repos}";
    public string? Kept => kept;
    public bool IsSecret => type == "secret" || Sprig.Engine.Planning.SecretValues.LooksSecret(name);
    public bool Required => otherwise is null;
    public string Hint => otherwise is null ? "Needs a value: nothing else gives one" : $"Empty: {otherwise}";

    [ObservableProperty] private string _value = kept ?? "";

    public char PasswordChar => IsSecret ? '•' : '\0';
}

public partial class WorkspacesViewModel
{
    /// <summary>The chosen orchestration or repo's settings, in the new-workspace form.</summary>
    public ObservableCollection<SettingInput> NewSettings { get; } = [];

    public bool HasNewSettings => NewSettings.Count > 0;

    int _newSettingsVersion;

    /// <summary>
    /// Each setting of what <paramref name="loaded"/> runs, with what it gets when no value is set on the workspace.
    /// The plan says which nothing gives a value; those need one before the workspace can come up.
    /// </summary>
    public IReadOnlyList<SettingInput> SettingInputs(LoadedOrchestration loaded, IReadOnlyDictionary<string, string>? kept)
    {
        var plan = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, "settings", _services.Settings.Get(), new HashSet<int>()));
        return loaded.Repos.Values
            .SelectMany(repo => repo.Settings.Select(setting => (repo, setting)))
            .GroupBy(x => x.setting.Name)
            .Select(g =>
            {
                var (repo, setting) = g.First();
                var others = loaded.Repos.Values.Where(r => r.Exports.Any(e => e.Name == g.Key) && g.All(x => x.repo != r)).Select(r => r.Name).ToArray();
                var otherwise = plan.ToAsk.Any(a => a.Name == g.Key) ? null
                    : loaded.Orchestration!.Bindings.Any(b => b.Setting == g.Key) ? "the orchestration's"
                    : others.Length > 0 ? $"{others[0]}'s"
                    : setting.Default is { } d ? ReposViewModel.DescribeDefault(d)
                    : null;
                return new SettingInput(g.Key, setting.Type, string.Join(", ", g.Select(x => x.repo.Name)), otherwise, kept?.GetValueOrDefault(g.Key));
            })
            .ToArray();
    }

    /// <summary>Read the chosen orchestration or repo's settings for the form. One that isn't approved yet shows none
    /// until it is; making the workspace asks for approval, then for any value it still needs.</summary>
    async Task LoadNewSettingsAsync(string? id)
    {
        var version = ++_newSettingsVersion;
        if (id is null || _services.Orchestrations.Resolve(id) is not { } path)
        {
            ReplaceNewSettings([]);
            return;
        }
        IReadOnlyList<SettingInput> inputs = [];
        try
        {
            var loaded = _newLoadedFor == id ? _newLoaded : null;
            if (loaded is null)
            {
                loaded = await AppServices.RunAsync(() => _services.Loader.Load(path));
                if (version != _newSettingsVersion) return;
                _newLoaded = loaded.Orchestration is null ? null : loaded;
                _newLoadedFor = id;
                // Now the slot's folders can be looked at too.
                CheckSlotSoon(immediately: true);
            }
            if (NewBranchChoices.Count == 0) await LoadBranchChoicesAsync(loaded);
            if (version != _newSettingsVersion) return;
            if (loaded.Orchestration is not null && loaded.Untrusted.Count == 0 && loaded.Problems.Count == 0)
            {
                // A slot being reused starts from the values it kept, each of which can be changed or cleared.
                var kept = _newSlotRecord?.Values;
                inputs = await AppServices.RunAsync(() => SettingInputs(loaded, kept));
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
        if (version != _newSettingsVersion) return;
        ReplaceNewSettings(inputs);
    }

    /// <summary>Swap the form's settings in one go, and only when they differ, so the list doesn't collapse and regrow
    /// while it's reloaded. A value typed into a box carries over to the same setting.</summary>
    void ReplaceNewSettings(IReadOnlyList<SettingInput> inputs)
    {
        if (inputs.Select(i => (i.Name, i.Kept)).SequenceEqual(NewSettings.Select(i => (i.Name, i.Kept)))) return;
        var typed = NewSettings.Where(s => s.Value != (s.Kept ?? "")).ToDictionary(s => s.Name, s => s.Value);
        foreach (var input in inputs)
            if (typed.TryGetValue(input.Name, out var value)) input.Value = value;
        NewSettings.Clear();
        foreach (var input in inputs) NewSettings.Add(input);
        OnPropertyChanged(nameof(HasNewSettings));
    }

    /// <summary>The values typed in the form, or null (after saying why) when one it needs is empty.</summary>
    IReadOnlyDictionary<string, string>? NewValues(LoadedOrchestration loaded)
    {
        // Settings the form didn't show (it hadn't run the definitions yet) are added now, for the user to fill in.
        var shown = NewSettings.Select(s => s.Name).ToHashSet();
        var missing = SettingInputs(loaded, _newSlotRecord?.Values).Where(s => !shown.Contains(s.Name)).ToArray();
        foreach (var input in missing) NewSettings.Add(input);
        if (missing.Length > 0) OnPropertyChanged(nameof(HasNewSettings));

        var empty = NewSettings.Where(s => s.Required && s.Value.Length == 0).Select(s => s.Name).ToArray();
        if (empty.Length > 0)
        {
            Fail($"Give {string.Join(" and ", empty)} a value: nothing else gives {(empty.Length == 1 ? "it" : "them")} one.");
            return null;
        }
        return NewSettings.Where(s => s.Value.Length > 0).ToDictionary(s => s.Name, s => s.Value);
    }

    // ---- the selected workspace's settings ----

    public ObservableCollection<SettingInput> WorkspaceSettings { get; } = [];

    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _settingsMessage = "";

    public bool HasWorkspaceSettings => WorkspaceSettings.Count > 0;

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Selected?.Record is not { } record || record.IsMain) return;
        var loaded = await LoadAsync(record.OrchestrationPath, OpenSettings, record.Name);
        if (loaded is null) return;
        WorkspaceSettings.Clear();
        foreach (var input in await AppServices.RunAsync(() => SettingInputs(loaded, record.Values))) WorkspaceSettings.Add(input);
        OnPropertyChanged(nameof(HasWorkspaceSettings));
        SettingsMessage = WorkspaceSettings.Count == 0 ? "Its repos have no inputs." : "";
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CancelSettings() => IsSettingsOpen = false;

    /// <summary>Keep the changed values on the workspace; one that's ready is made ready again (starting nothing) so its
    /// files have them, and the apps whose values changed are named for a restart.</summary>
    [RelayCommand]
    private async Task SaveSettings()
    {
        if (Selected?.Record is not { } record) return;
        var set = WorkspaceSettings.Where(s => s.Value.Length > 0 && s.Value != s.Kept).ToDictionary(s => s.Name, s => s.Value);
        var unset = WorkspaceSettings.Where(s => s.Value.Length == 0 && s.Kept is not null).Select(s => s.Name).ToArray();
        var empty = WorkspaceSettings.Where(s => s.Required && s.Value.Length == 0).Select(s => s.Name).ToArray();
        if (empty.Length > 0)
        {
            SettingsMessage = $"Give {string.Join(" and ", empty)} a value: nothing else gives {(empty.Length == 1 ? "it" : "them")} one.";
            return;
        }
        IsSettingsOpen = false;
        if (set.Count == 0 && unset.Length == 0) return;

        var before = _services.Workspaces.LastPlan(record.Orchestration, record.Name);
        await AppServices.RunAsync(() => _services.Workspaces.SetValues(record.Orchestration, record.Name, set, unset));
        Refresh();
        if (Selected?.IsUp != true)
        {
            AppsMessage = "Kept. It uses them the next time it starts.";
            return;
        }
        if (!await UpAsync(retry: SaveSettings)) return;
        var after = _services.Workspaces.LastPlan(record.Orchestration, record.Name);
        var changed = ChangedApps(before, after);
        AppsMessage = changed.Count == 0 ? "Its files have the new values."
            : $"Its files have the new values. Restart {string.Join(", ", changed)} to use them.";
    }

    /// <summary>Apps whose values differ between the two plans.</summary>
    public static IReadOnlyList<string> ChangedApps(Plan? before, Plan? after) => (after?.Apps ?? [])
        .Where(a => before?.Apps.FirstOrDefault(b => b.Key == a.Key) is not { } old
                    || !old.Env.Select(e => (e.Key, e.File, e.Value.Value)).SequenceEqual(a.Env.Select(e => (e.Key, e.File, e.Value.Value))))
        .Select(a => a.Name)
        .ToArray();
}
