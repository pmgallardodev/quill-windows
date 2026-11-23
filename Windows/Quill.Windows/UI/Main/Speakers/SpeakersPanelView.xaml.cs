using System.Windows;

namespace Quill.Windows.UI;

internal partial class SpeakersPanelView
{
    public SpeakersPanelView()
    {
        InitializeComponent();
    }

    /// <summary>Opens "Voces guardadas" as a dialog of the main window, then refreshes the panel.</summary>
    private async void OnSavedVoicesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakersPanelViewModel panel)
        {
            return;
        }

        var viewModel = new SavedVoicesViewModel(panel.Voices, WpfUiShell.Instance);
        var window = new SavedVoicesWindow(viewModel) { Owner = Window.GetWindow(this) };
        await viewModel.LoadAsync();
        window.ShowDialog();
        await panel.ReloadAsync();
    }
}
