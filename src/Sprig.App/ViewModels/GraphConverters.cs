using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Sprig.App.ViewModels;

/// <summary>Converters for the branch graph. <see cref="SelectedBorder"/> gives a pill a 2px outline when its
/// ref name matches the current graph selection (values: [refName, selectedRef]) — so the selected chip picks
/// up the same highlight as the selected node.</summary>
public static class GraphConverters
{
    public static readonly IMultiValueConverter SelectedBorder = new SelectedBorderConverter();

    sealed class SelectedBorderConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var selected = values.Count >= 2 && values[0] is string name && values[1] is string sel
                && string.Equals(name, sel, StringComparison.Ordinal);
            return new Thickness(selected ? 2 : 0);
        }
    }
}
