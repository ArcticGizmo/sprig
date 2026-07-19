using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    public MainWindowViewModel(AppServices services)
    {
        Pages =
        [
            new WorkspacesViewModel(services),
            new ReposViewModel(services),
            new StacksViewModel(services),
        ];
        _currentPage = Pages[0];
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;
}
