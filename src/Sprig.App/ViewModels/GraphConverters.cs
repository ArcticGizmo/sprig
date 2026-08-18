using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sprig.App.ViewModels;

/// <summary>Converters for the branch graph. <see cref="SelectedBorderBrush"/> colours a pill's (always 2px)
/// border amber when its ref matches the current graph selection, else transparent (values: [refName,
/// selectedRef]). Keeping the thickness constant means selecting never reflows the pill text.</summary>
public static class GraphConverters
{
    static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F2C94C"));

    public static readonly IMultiValueConverter SelectedBorderBrush = new SelectedBorderBrushConverter();

    sealed class SelectedBorderBrushConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var selected = values.Count >= 2 && values[0] is string name && values[1] is string sel
                && string.Equals(name, sel, StringComparison.Ordinal);
            return selected ? Amber : Brushes.Transparent;
        }
    }
}
