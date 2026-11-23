using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using Quill.Windows.Resources;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>
/// Clickable timestamp ("12:34", "1:02:03") that jumps to a point in the meeting. It is a
/// button: bind <c>Command</c> and <c>CommandParameter</c> (usually the same time) or handle
/// <c>Click</c>. <see cref="Time"/> accepts the summary contract's "hh:mm:ss" strings through
/// WPF's TimeSpan conversion.
/// </summary>
public class TimestampLink : WpfButtonBase
{
    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(TimeSpan), typeof(TimestampLink), new PropertyMetadata(TimeSpan.Zero, OnTimeChanged));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(TimestampLink), new PropertyMetadata("00:00"));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    static TimestampLink()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TimestampLink), new FrameworkPropertyMetadata(typeof(TimestampLink)));
    }

    public TimestampLink()
    {
        UpdateText();
    }

    public TimeSpan Time
    {
        get => (TimeSpan)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);

    /// <summary>"mm:ss" under an hour, "h:mm:ss" from an hour on. Negative values clamp to zero.</summary>
    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes:00}:{time.Seconds:00}");
    }

    private static void OnTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimestampLink)d).UpdateText();

    private void UpdateText()
    {
        var text = Format(Time);
        SetValue(DisplayTextPropertyKey, text);
        AutomationProperties.SetName(this, Loc.Format("A11y_JumpToTimestamp", text));
    }
}
