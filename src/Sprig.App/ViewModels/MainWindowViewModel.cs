using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;
using Sprig.Engine.Store;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>Every navigable page, including the two pinned to the bottom of the nav.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows, in workflow order. Each row's subtitle says what it holds, so they need no section headers.</summary>
    public IReadOnlyList<PageViewModel> NavItems { get; }

    public HomeViewModel Home { get; }
    public ReposViewModel Repos { get; }
    public WorkspacesViewModel Workspaces { get; }
    public UsageViewModel Usage { get; }
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

    readonly UiState _ui;

    /// <summary>The nav folded down to its icons, so a log can have the width.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavToggleTip))]
    private bool _navCollapsed;

    public string NavToggleTip => NavCollapsed ? "Expand the menu" : "Collapse the menu to its icons";

    [RelayCommand]
    private void ToggleNav() => _ui.NavCollapsed = NavCollapsed = !NavCollapsed;

    /// <summary>The app-wide host poll; the window's timer drives it.</summary>
    public HostStatus HostStatus { get; }

    /// <summary>Told by the window whether it shows, so docker isn't asked on behalf of a hidden window.</summary>
    public InfraUsage InfraUsage { get; }

    public bool ShowUpdateNotice => !string.IsNullOrEmpty(UpdateNotice);

    public bool IsDevInstance => AppProfile.IsDev;

    public static string WindowTitle => "Sprig" + AppProfile.DisplaySuffix;

    /// <summary>Names the workspace shown, so windows on different desktops can be told apart in Task View and
    /// Alt-Tab.</summary>
    public string Title => ShownWorkspace is not null && Workspaces.Selected is { } shown
        ? $"{shown.Title} · {shown.Branch} — {WindowTitle}"
        : WindowTitle;

    /// <summary>The key of the workspace this window shows; null when it shows something else.</summary>
    public string? ShownWorkspace => ReferenceEquals(CurrentPage, Workspaces) ? Workspaces.Selected?.Record.Key : null;

    /// <summary>Open another window, on a workspace by its key or on Home; wired by whoever keeps the windows.</summary>
    public Action<string?>? NewWindow { get; set; }

    /// <param name="checkForUpdates">Tests pass false so constructing the window never touches the network. Later
    /// windows pass false too and are handed the first one's answer with <see cref="ApplyUpdate"/>.</param>
    public MainWindowViewModel(AppServices services, bool checkForUpdates = true)
    {
        HostStatus = services.HostStatus;
        InfraUsage = services.InfraUsage;
        _ui = services.Ui;
        _navCollapsed = _ui.NavCollapsed;
        Home = new HomeViewModel(services);
        Repos = new ReposViewModel(services);
        Workspaces = new WorkspacesViewModel(services);
        Usage = new UsageViewModel(services);
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
        Repos.OpenWorkspace = ShowWorkspace;
        Home.OpenWorkspace = ShowWorkspace;
        Usage.OpenWorkspace = ShowWorkspace;
        Workspaces.OpenInNewWindow = key => NewWindow?.Invoke(key);
        Workspaces.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspacesViewModel.Selected)) NotifyShown();
        };
        Home.AddRepo = () =>
        {
            CurrentPage = Repos;
            Repos.OpenChooseCommand.Execute(null);
        };

        Pages = [Home, Repos, Workspaces, Usage, Orchestrations, Settings, About];
        NavItems = [Home, Repos, Workspaces, Usage, Orchestrations];

        _currentPage = Home;
        Home.IsActive = true;

        if (checkForUpdates) _ = CheckForUpdatesAsync();
    }

    /// <summary>One workspace's page, by its key.</summary>
    public void ShowWorkspace(string key)
    {
        CurrentPage = Workspaces;
        Workspaces.Show(key);
    }

    [RelayCommand]
    private void OpenNewWindow() => NewWindow?.Invoke(null);

    void NotifyShown()
    {
        OnPropertyChanged(nameof(ShownWorkspace));
        OnPropertyChanged(nameof(Title));
    }

    public void Dispose()
    {
        foreach (var page in Pages) page.Dispose();
    }

    /// <summary>Workspaces in the nav always lands on the list of them, the one place a workspace is picked from. A page
    /// that isn't ready stays shut.</summary>
    [RelayCommand]
    private void Navigate(PageViewModel page)
    {
        if (!page.IsReady) return;
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

    public void WindowFocused() => CurrentPage.WindowFocused();

    partial void OnUpdateNoticeChanged(string? value) => OnPropertyChanged(nameof(ShowUpdateNotice));

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var page in Pages)
            page.IsActive = ReferenceEquals(page, value);
        NotifyShown();
    }

    /// <summary>What the first window's launch check found, raised once it has an answer.</summary>
    public event Action<UpdateCheckResult>? UpdateChecked;

    public UpdateCheckResult? UpdateResult => _updateResult;

    async Task CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckDetailedAsync();
        ApplyUpdate(result);
        UpdateChecked?.Invoke(result);
    }

    public void ApplyUpdate(UpdateCheckResult result)
    {
        _updateResult = result;
        if (result.Availability == UpdateAvailability.Available)
            UpdateNotice = $"Update available: v{result.AvailableVersion} (you have v{result.CurrentVersion})";
    }
}
