using Avalonia.Data.Converters;
using Avalonia.Media;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

/// <summary>Each nav row's icon, which is all that's left of the row when the nav is collapsed, so each has to be told
/// apart at a glance. Drawn on a 24px grid and filled, even-odd, so cut-outs read as holes.</summary>
public static class NavIcons
{
    static readonly Geometry Home = Geometry.Parse("M12 3l9 8h-3v9h-4.5v-6h-3v6H6v-9H3z");
    static readonly Geometry Repos = Geometry.Parse("M3 5h7l2 2h9v12H3z");
    static readonly Geometry Workspaces = Geometry.Parse("M3 4h8v7H3z M13 4h8v7h-8z M3 13h8v7H3z M13 13h8v7h-8z");
    static readonly Geometry Orchestrations = Geometry.Parse("M3 3h6v6H3z M15 3h6v6h-6z M9 15h6v6H9z M9 5.25h6v1.5H9z M11.25 6.75h1.5v8.25h-1.5z");
    static readonly Geometry Other = Geometry.Parse("M4 4h16v16H4z");

    public static readonly Geometry Settings = Geometry.Parse("M3 6h18v2H3z M3 11h18v2H3z M3 16h18v2H3z M6 4h3v6H6z M14 9h3v6h-3z M9 14h3v6H9z");
    public static readonly Geometry About = Geometry.Parse("F0 M12 2a10 10 0 1 1 0 20a10 10 0 1 1 0-20z M11 10h2v7h-2z M11 6h2v2h-2z");
    public static readonly Geometry Update = Geometry.Parse("M11 3h2v9.2l3.3-3.3 1.4 1.4L12 16 6.3 10.3l1.4-1.4L11 12.2z M4 18h16v2H4z");
    public static readonly Geometry Collapse = Geometry.Parse("M11.6 6.4L10.2 5 3.2 12l7 7 1.4-1.4L6 12z M19.6 6.4L18.2 5l-7 7 7 7 1.4-1.4L14 12z");
    public static readonly Geometry Expand = Geometry.Parse("M12.4 6.4L13.8 5l7 7-7 7-1.4-1.4L18 12z M4.4 6.4L5.8 5l7 7-7 7-1.4-1.4L10 12z");

    public static readonly IValueConverter ForPage = new FuncValueConverter<PageViewModel?, Geometry>(page => page switch
    {
        HomeViewModel => Home,
        ReposViewModel => Repos,
        WorkspacesViewModel => Workspaces,
        OrchestrationsViewModel => Orchestrations,
        _ => Other,
    });

    public static readonly IValueConverter ForCollapsed = new FuncValueConverter<bool, Geometry>(collapsed => collapsed ? Expand : Collapse);
}
