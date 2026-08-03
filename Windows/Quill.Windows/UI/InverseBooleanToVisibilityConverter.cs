using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Quill.Windows.UI;

/// <summary>
/// WPF trae el converter directo pero no el inverso, y la lista de sesiones y los
/// resultados de busqueda ocupan la misma columna: uno se ve cuando el otro no.
/// </summary>
internal sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
