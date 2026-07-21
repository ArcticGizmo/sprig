using CommunityToolkit.Mvvm.ComponentModel;

namespace Sprig.App.ViewModels;

/// <summary>Base for all view-models (INotifyPropertyChanged via CommunityToolkit.Mvvm).</summary>
public abstract class ViewModelBase : ObservableObject;

/// <summary>A top-level navigable page.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    /// <summary>Nav label / header for the page.</summary>
    public abstract string Title { get; }

    /// <summary>True when this is the page currently shown — drives the left-nav highlight.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Count shown as a badge next to the nav label (0 = hidden). Each page sets it as it loads.</summary>
    [ObservableProperty] private int _navCount;

    /// <summary>Whether the nav badge should be shown (hidden at zero — e.g. Home never has a count).</summary>
    public bool ShowNavCount => NavCount > 0;

    partial void OnNavCountChanged(int value) => OnPropertyChanged(nameof(ShowNavCount));
}
