using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sprig.App.Changelog;
using Sprig.Core.Changelog;

namespace Sprig.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        ViewChangelogButton.Click += OpenChangelog;
    }

    // Opens the full changelog in a viewer window (all sections, no post-update suppress action).
    void OpenChangelog(object? sender, RoutedEventArgs e)
    {
        var markdown = ChangelogMarkdown.LoadEmbedded();
        var sections = markdown is null
            ? []
            : ChangelogParser.Parse(markdown).ToList();

        var window = new ChangelogWindow("What's new in sprig", "Recent releases", sections);
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
