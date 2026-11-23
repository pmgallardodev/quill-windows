using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Quill.Windows.UI.Companion;

/// <summary>True → Collapsed, false → Visible. Kept here so the panel does not depend on main-window code.</summary>
internal sealed class CompanionInverseVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}

/// <summary>Glyph in front of a live summary item: ✓ decision, ☐ task, ? open question.</summary>
internal sealed class SummaryItemGlyphConverter : IValueConverter
{
    public static string GlyphOf(SummaryItemKind kind) => kind switch
    {
        SummaryItemKind.Decision => "✓",
        SummaryItemKind.ActionItem => "☐",
        _ => "?",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SummaryItemKind kind ? GlyphOf(kind) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
