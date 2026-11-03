using System;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Sprig.App.ViewModels;

namespace Sprig.App.Controls;

/// <summary>A log line in the colours the app printed it in. Sprig's own lines are dimmed so the app's stand out.</summary>
public sealed class AnsiLine : TextBlock
{
    public static readonly StyledProperty<LogLine?> LineProperty = AvaloniaProperty.Register<AnsiLine, LogLine?>(nameof(Line));

    static readonly ConcurrentDictionary<int, IBrush> Brushes = new();

    public LogLine? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty) Rebuild();
    }

    void Rebuild()
    {
        Inlines ??= [];
        Inlines.Clear();
        if (Line is not { } line) return;
        Opacity = line.IsSprig ? 0.6 : 1;
        foreach (var part in line.Runs)
        {
            var run = new Run(part.Text);
            if (part.Foreground is { } rgb) run.Foreground = Brushes.GetOrAdd(rgb, c => new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)));
            if (part.Bold) run.FontWeight = FontWeight.SemiBold;
            if (part.Dim) run.Foreground = Brushes.GetOrAdd(0x8C8CA0, _ => new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0xA0)));
            Inlines.Add(run);
        }
    }
}
