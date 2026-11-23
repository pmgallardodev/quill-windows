using System.Windows;
using System.Windows.Controls.Primitives;
using Quill.Windows.Resources;
// UseWindowsForms brings an implicit using of System.Windows.Forms, with its own TextBox,
// MessageBox and KeyEventArgs. The aliases avoid the ambiguity.
using WpfCore = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

namespace Quill.Windows.UI;

internal sealed partial class MeetingDetailView : WpfControls.UserControl
{
    public MeetingDetailView()
    {
        InitializeComponent();
    }

    private MeetingDetailViewModel? ViewModel => DataContext as MeetingDetailViewModel;

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu)
        {
            return;
        }

        // Opened by hand, the menu does not inherit the button's DataContext.
        menu.DataContext = DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnTitleEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TitleEditor.Focus();
                TitleEditor.SelectAll();
            });
        }
    }

    private void OnTitleKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (ViewModel?.Row is not { } row)
        {
            return;
        }

        if (e.Key == WpfInput.Key.Enter)
        {
            e.Handled = true;
            Commit(row);
        }
        else if (e.Key == WpfInput.Key.Escape)
        {
            e.Handled = true;
            row.CancelRename();
        }
    }

    /// <summary>
    /// Leaving the box saves, like Enter. Losing focus without saving is the easiest way to
    /// believe you renamed something that stayed as it was.
    /// </summary>
    private void OnTitleLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Row is { } row)
        {
            Commit(row);
        }
    }

    private void OnNotesLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } detail)
        {
            _ = detail.Notes.FlushAsync();
        }
    }

    private static void Commit(MeetingRowViewModel row)
    {
        if (!row.CommitRename())
        {
            WpfCore.MessageBox.Show(
                Loc.Get("Meeting_RenameFailed"),
                "Quill",
                WpfCore.MessageBoxButton.OK,
                WpfCore.MessageBoxImage.Warning);
        }
    }
}
