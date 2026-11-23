using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>Formatting shared by the main window view models.</summary>
internal static class UiFormat
{
    /// <summary>"12:04" or "1:02:05"; "--:--" for unknown durations.</summary>
    public static string Duration(TimeSpan value) =>
        value <= TimeSpan.Zero
            ? "--:--"
            : value.TotalHours >= 1
                ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
                : $"{value.Minutes}:{value.Seconds:D2}";

    /// <summary>Local date/time with the format stored under <paramref name="formatKey"/>, first letter upper-cased.</summary>
    public static string Date(DateTimeOffset value, string formatKey)
    {
        var culture = Loc.Culture ?? System.Globalization.CultureInfo.CurrentUICulture;
        var text = value.ToLocalTime().ToString(Loc.Get(formatKey), culture);
        return text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
    }
}
