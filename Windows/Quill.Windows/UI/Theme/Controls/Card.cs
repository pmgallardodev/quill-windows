using System.Windows;
using WpfContentControl = System.Windows.Controls.ContentControl;

namespace Quill.Windows.UI.Theme.Controls;

/// <summary>
/// Rounded surface for grouping content (a summary section, a meeting row, a panel block).
/// Look comes from the implicit style in Controls.xaml; set <see cref="IsInteractive"/> to get
/// a hover fill for clickable cards.
/// </summary>
public class Card : WpfContentControl
{
    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(Card), new PropertyMetadata(false));

    static Card()
    {
        // Own key so the WPF-UI ContentControl style does not apply.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));
        FocusableProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(false));
    }

    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }
}
