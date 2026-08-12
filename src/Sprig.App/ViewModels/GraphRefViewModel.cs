using Avalonia.Media;

namespace Sprig.App.ViewModels;

/// <summary>A branch pill in a graph row, colour-coded by kind: the current branch, a likely-default
/// (main/master), or any other branch. Clicking it selects that branch as the start point.</summary>
public sealed class GraphRefViewModel(string name, GraphRefViewModel.RefKind kind)
{
    public enum RefKind { Current, Default, Other }

    public string Name => name;
    public RefKind Kind => kind;

    public IBrush Background => kind switch
    {
        RefKind.Current => new SolidColorBrush(Color.Parse("#27AE60")), // green — where you are now
        RefKind.Default => new SolidColorBrush(Color.Parse("#4C9AFF")), // blue — main/master
        _ => new SolidColorBrush(Color.Parse("#3A4250")),               // slate — everything else
    };

    public IBrush Foreground => kind == RefKind.Other
        ? Brushes.White
        : new SolidColorBrush(Color.Parse("#0B1220"));
}
