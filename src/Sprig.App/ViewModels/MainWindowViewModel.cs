using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Non-null when a newer version is available; drives the top notification bar.</summary>
    [ObservableProperty]
    private string? _updateNotice;

    public MainWindowViewModel(AppServices services)
    {
        Pages =
        [
            new WorkspacesViewModel(services),
            new ReposViewModel(services),
            new StacksViewModel(services),
        ];
        _currentPage = Pages[0];
        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    [RelayCommand]
    private void DismissUpdateNotice() => UpdateNotice = null;

    async Task CheckForUpdatesAsync() => UpdateNotice = await UpdateChecker.CheckAsync();
}
