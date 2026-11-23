using System.ComponentModel;
using System.Windows;
// UseWindowsForms inyecta un using implicito de System.Windows.Forms, que trae sus
// propios TextBox, MessageBox y KeyEventArgs. Los alias evitan la ambiguedad.
using WpfCore = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

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

    private void OnTitleKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (sender is not WpfControls.TextBox { DataContext: SessionRowViewModel row })
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
    /// Salir del cuadro guarda, igual que Enter. Perder el foco sin guardar es la
    /// forma mas facil de creer que renombraste algo que sigue como estaba.
    /// </summary>
    private void OnTitleLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is WpfControls.TextBox { DataContext: SessionRowViewModel row })
        {
            Commit(row);
        }
    }

    private static void Commit(SessionRowViewModel row)
    {
        if (!row.CommitRename())
        {
            WpfCore.MessageBox.Show(
                "No se pudo guardar el nombre. Comprueba que la carpeta de la sesion sigue accesible.",
                "quill",
                WpfCore.MessageBoxButton.OK,
                WpfCore.MessageBoxImage.Warning);
        }
    }
}
