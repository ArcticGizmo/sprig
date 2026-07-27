using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Coach;

namespace Sprig.App.ViewModels;

/// <summary>
/// The "Learn" page: the library of guided lessons, each teaching one concept by hand-holding the user
/// through doing it in the throwaway sandbox. Read-only projection of the guide catalog plus the user's
/// completed-guide list from settings; launching a guide is routed through the navigator (only the window
/// knows a store swap is involved).
/// </summary>
public partial class LearnViewModel : PageViewModel
{
    readonly AppServices _services;
    readonly Navigator _nav;

    public LearnViewModel(AppServices services, Navigator nav)
    {
        _services = services;
        _nav = nav;
        Reload();
    }

    public override string Title => "Learn";

    public ObservableCollection<GuideItemViewModel> Guides { get; } = [];

    protected override void OnActivated() => Reload();

    void Reload()
    {
        // Completion lives in the real store; when a guide is running the app is on the demo store, but the
        // Learn list is only ever shown on the real one, so reading this store's settings is correct.
        var done = _services.Settings.Get().CompletedGuides.ToHashSet();
        Guides.Clear();
        foreach (var guide in Coach.Guides.All)
            Guides.Add(new GuideItemViewModel(guide, done.Contains(guide.Id), _nav));
    }

    /// <summary>Refresh ticks after returning from a guide (the page re-activates, but be explicit too).</summary>
    public void Refresh() => Reload();
}

/// <summary>One lesson in the Learn list: its metadata, whether it's done, and how to start it.</summary>
public partial class GuideItemViewModel(Guide guide, bool completed, Navigator nav) : ViewModelBase
{
    public string Title => guide.Title;
    public string Subtitle => guide.Subtitle;
    public string Duration => guide.Duration;
    public bool Completed { get; } = completed;
    public string ActionLabel => Completed ? "Replay" : "Start";

    [RelayCommand]
    private void Start() => nav.EnterGuide(guide);
}
