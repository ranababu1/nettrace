using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetTrace.App.Converters;

/// <summary>Visible when the bound count is zero; used for empty-state messages.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
