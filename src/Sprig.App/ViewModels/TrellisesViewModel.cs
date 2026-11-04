using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;
using Sprig.Model;

namespace Sprig.App.ViewModels;

public sealed record TrellisItem(string Name, string Path)
{
    public string DisplayPath => Path.Replace('\\', '/');
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
    public bool Missing => !File.Exists(Path);
}

/// <param name="Changed">An earlier version was approved; this content wasn't.</param>
/// <param name="TrustPath">Where the approval is kept: the repo's own sprig.cs, for a copy in a sprout's worktree.</param>
public sealed record PendingTrustItem(string Path, string Sha256, bool Changed, string TrustPath)
{
    public static PendingTrustItem From(NeedsTrust p) => new(p.Path.Replace('\\', '/'), p.Sha256, p.ApprovedSha256 is not null, p.TrustPath);

    public string Why => (Changed, Path != TrustPath.Replace('\\', '/')) switch
    {
        (false, _) => "not approved yet",
        (true, false) => "changed since you approved it",
        _ => "the sprout's branch changes it; this version isn't approved yet",
    };
}

/// <summary>
/// The Trellises page: register trellis files, and see what a sprout of the selected one would get. Definitions only
/// run once their exact content is approved; anything that isn't is listed for the user to approve first.
/// </summary>
public partial class TrellisesViewModel : PageViewModel
{
    readonly AppServices _services;
    Func<Task>? _afterApproval;

    public override string Title => "Trellises";

    public ObservableCollection<TrellisItem> Trellises { get; } = [];
    public ObservableCollection<PendingTrustItem> PendingTrust { get; } = [];
    public ObservableCollection<ProblemRow> LoadProblems { get; } = [];

    [ObservableProperty] private TrellisItem? _selected;
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string _addMessage = "";
    [ObservableProperty] private bool _addFailed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private PlanView? _plan;
    [ObservableProperty] private bool _hasPendingTrust;
    [ObservableProperty] private bool _hasLoadProblems;

    public bool IsEmpty => Trellises.Count == 0;
    public bool HasSelection => Selected is not null;

    public TrellisesViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    protected override void OnActivated() => Refresh();

    void Refresh()
    {
        var keep = Selected?.Name;
        Trellises.Clear();
        foreach (var e in _services.Trellises.List()) Trellises.Add(new TrellisItem(e.Name, e.Path));
        NavCount = Trellises.Count;
        OnPropertyChanged(nameof(IsEmpty));
        Selected = Trellises.FirstOrDefault(t => t.Name == keep) ?? Selected ?? Trellises.FirstOrDefault();
    }

    partial void OnSelectedChanged(TrellisItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is not null) _ = PlanSelectedAsync();
    }

    [RelayCommand]
    private Task Replan() => PlanSelectedAsync();

    async Task PlanSelectedAsync()
    {
        if (Selected is not { } trellis) return;
        ClearResults();
        if (trellis.Missing)
        {
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, "the trellis file has moved or been deleted; remove it and add it again", new SourceLocation(trellis.Path, 0))], trellis.Path);
            return;
        }

        IsBusy = true;
        BusyText = "Running the definitions…";
        try
        {
            var loaded = await AppServices.RunAsync(() => _services.Loader.Load(trellis.Path));
            if (loaded.Untrusted.Count > 0)
            {
                AskForTrust(loaded.Untrusted, PlanSelectedAsync);
                return;
            }
            if (loaded.Trellis is null || loaded.Problems.Count > 0)
            {
                ShowLoadProblems(loaded.Problems, trellis.Path);
                return;
            }
            var settings = _services.Settings.Get();
            var plan = await AppServices.RunAsync(() =>
            {
                var problems = new System.Collections.Generic.List<PlanProblem>();
                var built = Planner.Build(new PlanInput(loaded.Trellis, loaded.Repos, "plan", settings, PortAllocator.Listening(),
                    ComposeFiles: loaded.ComposeFiles(problems)));
                if (!built.HasErrors) problems.AddRange(_services.Sprouts.CheckFiles(built, loaded.RepoDirs));
                return built with { Problems = [.. problems, .. built.Problems] };
            });
            Plan = new PlanView(plan, Path.GetDirectoryName(trellis.Path)!);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Add()
    {
        AddMessage = "";
        AddFailed = false;
        var path = NewPath.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            Fail("Choose a trellis file (a .trellis.cs) that exists.");
            return;
        }
        path = LongPath.Of(path);

        IsBusy = true;
        BusyText = "Reading the trellis…";
        DefinitionResult result;
        try { result = await AppServices.RunAsync(() => _services.Definitions.Evaluate(path)); }
        finally { IsBusy = false; }

        switch (result)
        {
            case NeedsTrust pending:
                AskForTrust([pending], Add);
                return;
            case Evaluated { Model: TrellisModel model }:
                _services.Trellises.Add(model.Name, path);
                NewPath = "";
                AddMessage = $"Added {model.Name}.";
                Refresh();
                Selected = Trellises.FirstOrDefault(t => t.Name == model.Name);
                return;
            case Evaluated:
                Fail("That file describes a repo (Repo.Describe), not a trellis.");
                return;
            case Failed failed:
                Fail($"It didn't run: {failed.Reason}. {FirstError(failed.Output)}");
                return;
            case Rejected rejected:
                Fail(string.Join(" ", rejected.Violations.Select(v => $"Line {v.Line}: {v.Reason}.")));
                return;
        }
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var item in PendingTrust) _services.Trust.Approve(item.TrustPath, item.Sha256);
        PendingTrust.Clear();
        HasPendingTrust = false;
        if (_afterApproval is { } next)
        {
            _afterApproval = null;
            await next();
        }
    }

    [RelayCommand]
    private void Remove(TrellisItem? item)
    {
        if (item is null) return;
        _services.Trellises.Remove(item.Name);
        if (Selected == item) Selected = null;
        ClearResults();
        Refresh();
    }

    void AskForTrust(System.Collections.Generic.IEnumerable<NeedsTrust> pending, Func<Task> then)
    {
        PendingTrust.Clear();
        foreach (var p in pending) PendingTrust.Add(PendingTrustItem.From(p));
        HasPendingTrust = PendingTrust.Count > 0;
        _afterApproval = then;
    }

    void ShowLoadProblems(System.Collections.Generic.IEnumerable<PlanProblem> problems, string trellisPath)
    {
        LoadProblems.Clear();
        foreach (var row in PlanView.Rows(problems, Path.GetDirectoryName(trellisPath)!)) LoadProblems.Add(row);
        HasLoadProblems = LoadProblems.Count > 0;
    }

    void ClearResults()
    {
        Plan = null;
        PendingTrust.Clear();
        HasPendingTrust = false;
        LoadProblems.Clear();
        HasLoadProblems = false;
    }

    void Fail(string message)
    {
        AddFailed = true;
        AddMessage = message;
    }

    static string FirstError(string output)
        => output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal)) ?? "";
}
