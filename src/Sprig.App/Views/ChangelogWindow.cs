using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sprig.App.Changelog;
using Sprig.Core.Changelog;

namespace Sprig.App.Views;

/// <summary>
/// A "What's new" card: a headline, a scrollable list of changelog sections, and buttons. Used two ways —
/// the post-update popup (with a "Don't show changelogs again" suppress action) and the always-available
/// viewer opened from the About page (no suppress action). Built in code so it can host the dynamically
/// rendered markdown from <see cref="ChangelogMarkdown"/>.
/// </summary>
public sealed class ChangelogWindow : Window
{
    public ChangelogWindow(string headline, string subhead, IReadOnlyList<ChangelogSection> sections, Action? onSuppress = null)
    {
        Title = "sprig — What's new";
        Width = 540;
        Height = 620;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("FormBgBrush");
        Content = Build(headline, subhead, sections, onSuppress);
    }

    Control Build(string headline, string subhead, IReadOnlyList<ChangelogSection> sections, Action? onSuppress)
    {
        var title = new TextBlock
        {
            Text = headline, Foreground = Brush("TitleBrush"), FontWeight = FontWeight.SemiBold, FontSize = 18,
        };
        var sub = new TextBlock
        {
            Text = subhead, Foreground = Brush("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var header = new StackPanel { Children = { title, sub } };

        var body = new StackPanel();
        if (sections.Count == 0)
            body.Children.Add(new TextBlock
            {
                Text = "No changelog entries to show.", Foreground = Brush("MutedBrush"), FontSize = 13,
            });
        for (var i = 0; i < sections.Count; i++)
            ChangelogMarkdown.Render(body, sections[i].Block);

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 14),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (onSuppress is not null)
        {
            var suppress = FlatButton("Don't show changelogs again", accent: false);
            suppress.Click += (_, _) => { try { onSuppress(); } catch { /* best-effort */ } Close(); };
            buttons.Children.Add(suppress);
        }
        var close = FlatButton("Close", accent: true);
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24) };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);
        return grid;
    }

    static Button FlatButton(string text, bool accent) => new()
    {
        Content = text,
        Padding = new Thickness(14, 8),
        FontWeight = accent ? FontWeight.SemiBold : FontWeight.Normal,
        Background = Brush(accent ? "AccentBrush" : "ButtonBgBrush"),
        Foreground = accent ? new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20)) : Brush("FgBrush"),
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    static IBrush? Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var v) && v is IBrush b ? b : null;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
