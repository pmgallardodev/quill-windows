using System.Windows;
using Quill.Windows.Resources;
using WpfControl = System.Windows.Controls.Control;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>
/// Placeholder for an empty list or summary section. Defaults to "Ninguno" (design §7.1:
/// empty arrays render as "None"); set <see cref="Description"/> for a larger empty screen.
/// </summary>
public class EmptyState : WpfControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    static EmptyState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));
        FocusableProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
    }

    public EmptyState()
    {
        // Local default (not metadata) so it is resolved after resources are available.
        SetCurrentValue(TextProperty, Loc.Get("Common_None"));
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Optional second line; hidden when empty.</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
