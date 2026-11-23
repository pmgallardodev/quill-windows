using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Quill.Windows.Resources;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>
/// Thin horizontal audio level bar. Bind <see cref="Level"/> to a 0..1 value (peak or RMS,
/// already scaled by the caller); the fill turns <see cref="HotBrush"/> from
/// <see cref="HotThreshold"/> on to warn about clipping. Default size 64x4, stretches.
/// Drawn directly in OnRender, so it is cheap to update ~20 times per second.
/// </summary>
public class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceUnit));

    public static readonly DependencyProperty HotThresholdProperty = DependencyProperty.Register(
        nameof(HotThreshold), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.85d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceUnit));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HotBrushProperty = DependencyProperty.Register(
        nameof(HotBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public LevelMeter()
    {
        // Theme-following defaults; callers can still assign their own brushes.
        SetResourceReference(TrackBrushProperty, "Quill.Brush.MeterTrack");
        SetResourceReference(FillBrushProperty, "Quill.Brush.MeterLow");
        SetResourceReference(HotBrushProperty, "Quill.Brush.MeterHigh");
        AutomationProperties.SetName(this, Loc.Get("A11y_AudioLevel"));
        SnapsToDevicePixels = true;
    }

    /// <summary>Current level, 0..1 (values outside are clamped).</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double HotThreshold
    {
        get => (double)GetValue(HotThresholdProperty);
        set => SetValue(HotThresholdProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? HotBrush
    {
        get => (Brush?)GetValue(HotBrushProperty);
        set => SetValue(HotBrushProperty, value);
    }

    /// <summary>Clamps to 0..1; NaN becomes 0 so a bad sample never breaks rendering.</summary>
    public static double ClampUnit(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 64 : Math.Min(64, availableSize.Width), 4);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        var level = Level;
        if (level <= 0)
        {
            return;
        }

        var fill = level >= HotThreshold ? HotBrush : FillBrush;
        var fillWidth = Math.Max(height, width * level);
        drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, 0, Math.Min(fillWidth, width), height), radius, radius);
    }

    private static object CoerceUnit(DependencyObject d, object baseValue) => ClampUnit((double)baseValue);
}
