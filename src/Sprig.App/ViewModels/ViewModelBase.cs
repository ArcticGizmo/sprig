using CommunityToolkit.Mvvm.ComponentModel;

namespace Sprig.App.ViewModels;

/// <summary>Base for all view-models (INotifyPropertyChanged via CommunityToolkit.Mvvm).</summary>
public abstract class ViewModelBase : ObservableObject;

/// <summary>A top-level navigable page.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Nav label / header for the page.</summary>
    public abstract string Title { get; }
}
