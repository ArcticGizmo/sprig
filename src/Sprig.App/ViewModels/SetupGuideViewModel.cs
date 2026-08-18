using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// The guided "Set up sprig" strip: a progress spine over the repo → stack → workspace pipeline
/// that launches the real Add-repo / New-stack / New-workspace flows in order and auto-advances as
/// the store changes. Opt-in (started from Home) and skippable. It never re-implements a flow — it
/// drives the existing ones via the <see cref="Navigator"/>.
/// </summary>
public partial class SetupGuideViewModel : ViewModelBase
{
    readonly AppServices _services;
    readonly Navigator _nav;

    public SetupGuideViewModel(AppServices services, Navigator nav)
    {
        _services = services;
        _nav = nav;
        _services.StoreChanged += () => _ = RefreshAsync();
    }

    /// <summary>Whether the strip is showing.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private SetupState _state = new(0, 0, 0);

    partial void OnStateChanged(SetupState value)
    {
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(Cta));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(CanGoBack));
    }

    public bool IsComplete => State.Stage == SetupStage.Running;

    public int StepNumber => State.Stage switch
    {
        SetupStage.Empty => 1,
        SetupStage.ReposReady => 2,
        _ => 3,
    };

    /// <summary>A previous step exists to revisit (e.g. add another repo while wiring a stack).</summary>
    public bool CanGoBack => StepNumber > 1;

    public string StepCounter => IsComplete ? "All done" : $"Step {StepNumber} of 3";
    public string Heading => IsComplete ? "You're all set" : State.NextTitle;

    public string Hint => IsComplete
        ? "Your first workspace is running — reopen this guide any time from Home."
        : State.NextSub;

    public string Cta => IsComplete ? "Done" : State.NextCta;

    /// <summary>Recompute state and show the strip.</summary>
    public void Start()
    {
        _ = RefreshAsync();
        IsActive = true;
    }

    async Task RefreshAsync()
    {
        var (repos, maps, workspaces) = await AppServices.RunAsync(() =>
            (_services.Repos.List().Count, _services.Maps.List().Count, _services.Workspaces.List().Count));
        State = new SetupState(repos, maps, workspaces);
    }

    /// <summary>Do the current step: open its flow, or close the strip once everything's running.</summary>
    [RelayCommand]
    private void DoStep()
    {
        switch (State.Stage)
        {
            case SetupStage.Empty: _nav.AddRepo(); break;
            case SetupStage.ReposReady: _nav.NewMap(); break;
            case SetupStage.MapReady: _nav.NewWorkspace(); break;
            default: IsActive = false; break;
        }
    }

    /// <summary>Revisit the previous step's flow — e.g. add another repo while composing a map.</summary>
    [RelayCommand]
    private void Back()
    {
        switch (StepNumber)
        {
            case 3: _nav.NewMap(); break;
            case 2: _nav.AddRepo(); break;
        }
    }

    [RelayCommand] private void Skip() => IsActive = false;
}
