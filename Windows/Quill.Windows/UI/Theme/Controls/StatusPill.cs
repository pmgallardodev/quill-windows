using System.Windows;
using System.Windows.Media;
using Quill.Windows.Resources;
using Brush = System.Windows.Media.Brush;
using WpfControl = System.Windows.Controls.Control;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>Activity shown by a <see cref="StatusPill"/>.</summary>
public enum PillStatus
{
    Recording,
    Paused,
    Transcribing,
    Summarizing,
}

/// <summary>
/// Small rounded status badge with a colored dot: Grabando (red, pulsing unless the user
/// disabled animations), En pausa (amber), Transcribiendo (blue), Resumiendo (violet).
/// <see cref="Text"/> overrides the default label, e.g. "Grabando · 12:04".
/// </summary>
public class StatusPill : WpfControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(PillStatus), typeof(StatusPill), new PropertyMetadata(PillStatus.Recording, OnChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(StatusPill), new PropertyMetadata(null));

    public static readonly DependencyProperty TintBrushProperty = DependencyProperty.Register(
        nameof(TintBrush), typeof(Brush), typeof(StatusPill), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(StatusPill), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsPulsingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsPulsing), typeof(bool), typeof(StatusPill), new PropertyMetadata(false));

    public static readonly DependencyProperty IsPulsingProperty = IsPulsingPropertyKey.DependencyProperty;

    static StatusPill()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(typeof(StatusPill)));
        FocusableProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(false));
    }

    public StatusPill()
    {
        Refresh();
    }

    public PillStatus Status
    {
        get => (PillStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Optional label override; null uses the localized status name.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Dot and text color. Set by the control from the theme palette.</summary>
    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>Pill background tint. Set by the control from the theme palette.</summary>
    public Brush? TintBrush
    {
        get => (Brush?)GetValue(TintBrushProperty);
        set => SetValue(TintBrushProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);

    /// <summary>True while recording and Windows animations are enabled.</summary>
    public bool IsPulsing => (bool)GetValue(IsPulsingProperty);

    /// <summary>Resource key of the localized default label for a status.</summary>
    public static string LabelKey(PillStatus status) => status switch
    {
        PillStatus.Recording => "Status_Recording",
        PillStatus.Paused => "Status_Paused",
        PillStatus.Transcribing => "Status_Transcribing",
        PillStatus.Summarizing => "Status_Summarizing",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusPill)d).Refresh();

    private void Refresh()
    {
        var role = Status switch
        {
            PillStatus.Recording => "Recording",
            PillStatus.Paused => "Warning",
            PillStatus.Transcribing => "Info",
            _ => "Ai",
        };
        SetResourceReference(AccentBrushProperty, $"Quill.Brush.{role}");
        SetResourceReference(TintBrushProperty, $"Quill.Brush.{role}Subtle");
        SetValue(DisplayTextPropertyKey, string.IsNullOrEmpty(Text) ? Loc.Get(LabelKey(Status)) : Text);
        SetValue(IsPulsingPropertyKey, Status == PillStatus.Recording && SystemParameters.ClientAreaAnimation);
    }
}
