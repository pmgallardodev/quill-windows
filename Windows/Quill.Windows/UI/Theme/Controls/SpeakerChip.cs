using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Quill.Windows.Resources;
using Brush = System.Windows.Media.Brush;
using WpfControl = System.Windows.Controls.Control;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>
/// Compact speaker label with a colored dot. The color is stable per speaker id:
/// ids ending in a number ("S3", "speaker-3", "Speaker 3") map to palette slot N (wrapping
/// after 8); other ids are hashed; "me" or <see cref="IsMe"/> uses the Me color.
/// Without <see cref="Label"/> it shows "Yo", "Interlocutor N" or the raw id.
/// </summary>
public class SpeakerChip : WpfControl
{
    public const int PaletteSize = 8;

    public static readonly DependencyProperty SpeakerIdProperty = DependencyProperty.Register(
        nameof(SpeakerId), typeof(string), typeof(SpeakerChip), new PropertyMetadata(null, OnIdentityChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SpeakerChip), new PropertyMetadata(null, OnIdentityChanged));

    public static readonly DependencyProperty IsMeProperty = DependencyProperty.Register(
        nameof(IsMe), typeof(bool), typeof(SpeakerChip), new PropertyMetadata(false, OnIdentityChanged));

    public static readonly DependencyProperty SpeakerBrushProperty = DependencyProperty.Register(
        nameof(SpeakerBrush), typeof(Brush), typeof(SpeakerChip), new PropertyMetadata(null));

    public static readonly DependencyProperty TintBrushProperty = DependencyProperty.Register(
        nameof(TintBrush), typeof(Brush), typeof(SpeakerChip), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey DisplayLabelPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayLabel), typeof(string), typeof(SpeakerChip), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayLabelProperty = DisplayLabelPropertyKey.DependencyProperty;

    static SpeakerChip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SpeakerChip), new FrameworkPropertyMetadata(typeof(SpeakerChip)));
        FocusableProperty.OverrideMetadata(typeof(SpeakerChip), new FrameworkPropertyMetadata(false));
    }

    public SpeakerChip()
    {
        Refresh();
    }

    /// <summary>Stable speaker id from the transcript/summary (e.g. "S2", "me").</summary>
    public string? SpeakerId
    {
        get => (string?)GetValue(SpeakerIdProperty);
        set => SetValue(SpeakerIdProperty, value);
    }

    /// <summary>Display name override (a renamed speaker, e.g. "Juan").</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsMe
    {
        get => (bool)GetValue(IsMeProperty);
        set => SetValue(IsMeProperty, value);
    }

    /// <summary>Strong speaker color (dot). Set by the control from the theme palette.</summary>
    public Brush? SpeakerBrush
    {
        get => (Brush?)GetValue(SpeakerBrushProperty);
        set => SetValue(SpeakerBrushProperty, value);
    }

    /// <summary>Background tint of the chip. Set by the control from the theme palette.</summary>
    public Brush? TintBrush
    {
        get => (Brush?)GetValue(TintBrushProperty);
        set => SetValue(TintBrushProperty, value);
    }

    public string DisplayLabel => (string)GetValue(DisplayLabelProperty);

    /// <summary>True for the owner's id convention ("me", case-insensitive).</summary>
    public static bool IsMeId(string? speakerId) =>
        string.Equals(speakerId?.Trim(), "me", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the 1-based number at the end of the id ("S3" -> 3), or null.</summary>
    public static int? TrailingNumber(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            return null;
        }

        var text = speakerId.Trim();
        var start = text.Length;
        while (start > 0 && char.IsAsciiDigit(text[start - 1]))
        {
            start--;
        }

        return start < text.Length
            && int.TryParse(text.AsSpan(start), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? number
                : null;
    }

    /// <summary>Maps a speaker id to a palette slot 1..8, stable across runs and machines.</summary>
    public static int PaletteSlot(string? speakerId)
    {
        if (TrailingNumber(speakerId) is int number)
        {
            return ((number - 1) % PaletteSize) + 1;
        }

        // FNV-1a: string.GetHashCode is randomized per process, colors must not change between runs.
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(speakerId?.Trim().ToLowerInvariant() ?? string.Empty))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash % PaletteSize) + 1;
    }

    private static void OnIdentityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpeakerChip)d).Refresh();

    private void Refresh()
    {
        var isMe = IsMe || IsMeId(SpeakerId);
        if (isMe)
        {
            SetResourceReference(SpeakerBrushProperty, "Quill.Brush.Me");
            SetResourceReference(TintBrushProperty, "Quill.Brush.MeSubtle");
        }
        else if (string.IsNullOrWhiteSpace(SpeakerId))
        {
            SetResourceReference(SpeakerBrushProperty, "Quill.Brush.Neutral");
            SetResourceReference(TintBrushProperty, "Quill.Brush.NeutralSubtle");
        }
        else
        {
            SetResourceReference(SpeakerBrushProperty, $"Quill.Brush.Speaker{PaletteSlot(SpeakerId)}");
            SetResourceReference(TintBrushProperty, "Quill.Brush.NeutralSubtle");
        }

        SetValue(DisplayLabelPropertyKey, ResolveLabel(isMe));
    }

    private string ResolveLabel(bool isMe)
    {
        if (!string.IsNullOrWhiteSpace(Label))
        {
            return Label;
        }

        if (isMe)
        {
            return Loc.Get("Speaker_Me");
        }

        if (string.IsNullOrWhiteSpace(SpeakerId))
        {
            return Loc.Get("Speaker_Unknown");
        }

        return TrailingNumber(SpeakerId) is int number
            ? Loc.Format("Speaker_Numbered", number)
            : SpeakerId;
    }
}
