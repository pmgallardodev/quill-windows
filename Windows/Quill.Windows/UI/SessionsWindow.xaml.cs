using System.ComponentModel;
using System.Windows;

namespace Quill.Windows.UI;

internal sealed partial class SessionsWindow : Window
{
    private readonly SessionsViewModel _viewModel;

    public SessionsWindow(SessionsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Solo el apagado de la aplicacion cierra la ventana de verdad.</summary>
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

    /// <summary>Es una app de bandeja: cerrar oculta, no destruye.</summary>
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

    private void OnRefresh(object sender, RoutedEventArgs e) => _viewModel.Refresh();
}
