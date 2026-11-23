using Quill.Windows.Ask;
using WpfCore = System.Windows;
using WpfControls = System.Windows.Controls;

namespace Quill.Windows.UI;

/// <summary>
/// "Buscar en todas las reuniones" page: <c>&lt;local:LibrarySearchView /&gt;</c> anywhere in the main
/// window. Builds its <see cref="LibrarySearchViewModel"/> from <see cref="AskModule.Current"/>.
/// </summary>
internal sealed partial class LibrarySearchView : WpfControls.UserControl
{
    public LibrarySearchView()
    {
        InitializeComponent();
        var module = AskModule.Current;
        Root.DataContext = module is null ? null : new LibrarySearchViewModel(module);
        Root.Visibility = module is null ? WpfCore.Visibility.Collapsed : WpfCore.Visibility.Visible;
        Unavailable.Visibility = module is null ? WpfCore.Visibility.Visible : WpfCore.Visibility.Collapsed;
    }
}
