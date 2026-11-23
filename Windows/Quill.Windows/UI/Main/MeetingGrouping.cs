using System.Globalization;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>Day bucket a meeting falls into in the meetings list.</summary>
internal enum MeetingDayBucket
{
    Today,
    Yesterday,
    ThisWeek,
    Earlier,
}

/// <param name="Bucket">Relative bucket.</param>
/// <param name="Date">Calendar day of the meeting in the viewer's offset.</param>
internal readonly record struct MeetingDay(MeetingDayBucket Bucket, DateOnly Date);

/// <summary>
/// Groups meetings by day the way people talk about them: "Hoy", "Ayer", "Esta semana",
/// then one group per calendar date. Pure, so the boundaries are unit-tested.
/// </summary>
internal static class MeetingGrouping
{
    /// <summary>
    /// Classifies <paramref name="started"/> relative to <paramref name="now"/>, using the
    /// offset of <paramref name="now"/> as the viewer's local time. The week starts on Monday.
    /// A start in the future (clock skew) counts as today.
    /// </summary>
    public static MeetingDay Classify(DateTimeOffset started, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var day = DateOnly.FromDateTime(started.ToOffset(now.Offset).DateTime);

        if (day >= today)
        {
            return new MeetingDay(MeetingDayBucket.Today, today);
        }

        if (day == today.AddDays(-1))
        {
            return new MeetingDay(MeetingDayBucket.Yesterday, day);
        }

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday);
        return day >= weekStart
            ? new MeetingDay(MeetingDayBucket.ThisWeek, day)
            : new MeetingDay(MeetingDayBucket.Earlier, day);
    }

    /// <summary>
    /// Header text of a group. Earlier dates read "Lunes, 10 de noviembre", with the year
    /// only when it is not the current one.
    /// </summary>
    public static string Label(MeetingDay day, DateOnly today, CultureInfo? culture = null)
    {
        culture ??= Loc.Culture ?? CultureInfo.CurrentUICulture;
        return day.Bucket switch
        {
            MeetingDayBucket.Today => Loc.Get("Group_Today"),
            MeetingDayBucket.Yesterday => Loc.Get("Group_Yesterday"),
            MeetingDayBucket.ThisWeek => Loc.Get("Group_ThisWeek"),
            _ => Capitalize(
                day.Date.ToString(
                    Loc.Get(day.Date.Year == today.Year ? "Group_DateFormat" : "Group_DateFormatWithYear"),
                    culture),
                culture),
        };
    }

    /// <summary>Convenience for view models: bucket and label in one call.</summary>
    public static string LabelFor(DateTimeOffset started, DateTimeOffset now, CultureInfo? culture = null) =>
        Label(Classify(started, now), DateOnly.FromDateTime(now.DateTime), culture);

    private static string Capitalize(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}
