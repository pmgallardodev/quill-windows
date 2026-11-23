using Quill.Windows.UI.Theme;

namespace Quill.Windows.UI;

/// <summary>"Voces guardadas": the voices Quill remembers on this machine.</summary>
internal partial class SavedVoicesWindow
{
    public SavedVoicesWindow(SavedVoicesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        ThemeBootstrapper.Attach(this);
    }
}
