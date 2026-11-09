using CommunityToolkit.Mvvm.ComponentModel;

namespace Sprig.App.ViewModels;

/// <summary>Base for all view-models (INotifyPropertyChanged via CommunityToolkit.Mvvm).</summary>
public abstract class ViewModelBase : ObservableObject;

/// <summary>A top-level navigable page.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    /// <summary>Nav label / header for the page.</summary>
    public abstract string Title { get; }

    /// <summary>A few words under the nav label saying what the page holds; null for none.</summary>
    public virtual string? NavSubtitle => null;

    public bool HasNavSubtitle => NavSubtitle is not null;

    /// <summary>True when this is the page currently shown — drives the left-nav highlight.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Count shown as a badge next to the nav label (0 = hidden). Each page sets it as it loads.</summary>
    [ObservableProperty] private int _navCount;

    /// <summary>Whether the nav badge should be shown (hidden at zero — e.g. Home never has a count).</summary>
    public bool ShowNavCount => NavCount > 0;

    partial void OnNavCountChanged(int value) => OnPropertyChanged(nameof(ShowNavCount));

    partial void OnIsActiveChanged(bool value)
    {
        if (value) OnActivated();
    }

    /// <summary>Called when this page becomes the current page. Override to refresh live data.</summary>
    protected virtual void OnActivated() { }

    /// <summary>The window came back to the front, perhaps after a terminal changed the store. Only the page shown hears
    /// of it.</summary>
    public void WindowFocused()
    {
        if (IsActive) OnWindowFocused();
    }

    /// <summary>Reload what a terminal may have changed, unless a dialog or an edit is in progress, which a reload would
    /// lose.</summary>
    protected virtual void OnWindowFocused() { }
}
