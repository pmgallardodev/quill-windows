using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Quill.Windows.UI.Theme;
using Wpf.Ui.Controls;

namespace Quill.Windows.UI;

/// <summary>
/// Main window: meetings grouped by day on the left, the open meeting (or My tasks) on the
/// right. It is a tray app, so closing hides the window; only shutdown really closes it.
/// </summary>
internal sealed partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ThemeBootstrapper.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;

        // The list binds to the default view of Meetings, so grouping it here groups the list.
        var view = CollectionViewSource.GetDefaultView(viewModel.Meetings);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MeetingRowViewModel.DayGroup)));
    }

    /// <summary>Only application shutdown closes the window for real.</summary>
    public bool AllowClose { get; set; }

    public void ShowRefreshed()
    {
        _viewModel.Refresh();
        _viewModel.Start();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Closing hides: the tray keeps the app alive.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            _viewModel.Stop();
            return;
        }

        base.OnClosing(e);
    }
}
