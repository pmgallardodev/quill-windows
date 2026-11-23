using System.Globalization;
using System.Windows.Data;
using Quill.Core.Domain;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>Shows <see cref="SummaryLevel"/> values as "Breve" / "Detallado" in the level selector.</summary>
internal sealed class SummaryLevelLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SummaryLevel.Brief => Loc.Get("Summary_LevelBrief"),
        SummaryLevel.Detailed => Loc.Get("Summary_LevelDetailed"),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
