using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;
using Sprig.Engine.Store;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>Every navigable page, including the two pinned to the bottom of the nav.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows, in workflow order. Each row's subtitle says what it holds, so they need no section headers.</summary>
    public IReadOnlyList<PageViewModel> NavItems { get; }

    public HomeViewModel Home { get; }
    public ReposViewModel Repos { get; }
    public WorkspacesViewModel Workspaces { get; }
    public OrchestrationsViewModel Orchestrations { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Non-null when a newer version is available; drives the update entry in the left nav.</summary>
    [ObservableProperty]
    private string? _updateNotice;

    /// <summary>Held so "Update now" installs what the launch check found without checking again.</summary>
    UpdateCheckResult? _updateResult;

    public UpdateInstaller Updater { get; } = new();

    /// <summary>The app-wide host poll; the window's timer drives it.</summary>
    public HostStatus HostStatus { get; }

    public bool ShowUpdateNotice => !string.IsNullOrEmpty(UpdateNotice);

    public bool IsDevInstance => AppProfile.IsDev;

    public static string WindowTitle => "Sprig (experimental)" + AppProfile.DisplaySuffix;

    /// <param name="checkForUpdates">Tests pass false so constructing the window never touches the network.</param>
    public MainWindowViewModel(AppServices services, bool checkForUpdates = true)
    {
        HostStatus = services.HostStatus;
        Home = new HomeViewModel();
        Repos = new ReposViewModel(services);
        Workspaces = new WorkspacesViewModel(services);
        Orchestrations = new OrchestrationsViewModel(services);
        Settings = new SettingsViewModel(services);
        About = new AboutViewModel(Updater);

        // The pages link to each other: an orchestration to its repos, a repo to the orchestrations that use it, and an
        // orchestration or a repo to the form that starts a workspace of it.
        Repos.OpenOrchestration = id =>
        {
            CurrentPage = Orchestrations;
            Orchestrations.Show(id);
        };
        Orchestrations.OpenRepo = id =>
        {
            CurrentPage = Repos;
            Repos.Show(id);
        };
        void StartWorkspace(string id)
        {
            CurrentPage = Workspaces;
            Workspaces.NewOrchestration = id;
            Workspaces.OpenAddCommand.Execute(null);
        }
        Orchestrations.StartWorkspace = StartWorkspace;
        Repos.StartWorkspace = StartWorkspace;

        Pages = [Home, Repos, Workspaces, Orchestrations, Settings, About];
        NavItems = [Home, Repos, Workspaces, Orchestrations];

        _currentPage = Home;
        Home.IsActive = true;

        if (checkForUpdates) _ = CheckForUpdatesAsync();
    }

    /// <summary>Workspaces in the nav always lands on the list of them, the one place a workspace is picked from.</summary>
    [RelayCommand]
    private void Navigate(PageViewModel page)
    {
        CurrentPage = page;
        if (page == Workspaces) Workspaces.Show(null);
    }

    /// <summary>Install the available update, then restart. Only returns when the install failed.</summary>
    [RelayCommand]
    private async Task UpdateNow()
    {
        if (_updateResult is not { Availability: UpdateAvailability.Available }) return;
        if (!await Updater.InstallAsync(_updateResult))
            UpdateNotice = "Update failed to install. Try again from the About page.";
    }

    partial void OnUpdateNoticeChanged(string? value) => OnPropertyChanged(nameof(ShowUpdateNotice));

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var page in Pages)
            page.IsActive = ReferenceEquals(page, value);
    }

    async Task CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckDetailedAsync();
        _updateResult = result;

        if (result.Availability != UpdateAvailability.Available)
            return;

        UpdateNotice = $"Update available: v{result.AvailableVersion} (you have v{result.CurrentVersion})";
    }
}
