using System.Windows;
using WpfControl = System.Windows.Controls.Control;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>
/// Section title with an optional count and an optional right-aligned action slot,
/// e.g. "Decisiones  3" with a "Copiar" button.
/// </summary>
public class SectionHeader : WpfControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int?), typeof(SectionHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(SectionHeader), new PropertyMetadata(null));

    static SectionHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SectionHeader), new FrameworkPropertyMetadata(typeof(SectionHeader)));
        FocusableProperty.OverrideMetadata(typeof(SectionHeader), new FrameworkPropertyMetadata(false));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Item count shown next to the title; null hides it.</summary>
    public int? Count
    {
        get => (int?)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Optional content on the right (button, menu, toggle).</summary>
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }
}
