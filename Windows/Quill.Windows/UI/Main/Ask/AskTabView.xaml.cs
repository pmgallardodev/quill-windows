using Quill.Windows.Ask;
using WpfCore = System.Windows;
using WpfControls = System.Windows.Controls;

namespace Quill.Windows.UI;

/// <summary>
/// "Preguntar" tab of the meeting detail: <c>&lt;local:AskTabView SessionId="{Binding Row.SessionId}" /&gt;</c>.
/// Builds an <see cref="AskViewModel"/> for the meeting from <see cref="AskModule.Current"/>
/// and shows "unavailable" when the module is not wired.
/// </summary>
internal sealed partial class AskTabView : WpfControls.UserControl
{
    public static readonly WpfCore.DependencyProperty SessionIdProperty = WpfCore.DependencyProperty.Register(
        nameof(SessionId),
        typeof(string),
        typeof(AskTabView),
        new WpfCore.PropertyMetadata(null, (target, _) => ((AskTabView)target).Rebuild()));

    public AskTabView()
    {
        InitializeComponent();
        Rebuild();
    }

    public string? SessionId
    {
        get => (string?)GetValue(SessionIdProperty);
        set => SetValue(SessionIdProperty, value);
    }

    private void Rebuild()
    {
        if (AskPanel is null)
        {
            return;
        }

        (AskPanel.DataContext as AskViewModel)?.Cancel();
        var module = AskModule.Current;
        var available = module is not null && !string.IsNullOrWhiteSpace(SessionId);
        AskPanel.DataContext = available ? new AskViewModel(module!, SessionId) : null;
        AskPanel.Visibility = available ? WpfCore.Visibility.Visible : WpfCore.Visibility.Collapsed;
        Unavailable.Visibility = module is null ? WpfCore.Visibility.Visible : WpfCore.Visibility.Collapsed;
    }
}
