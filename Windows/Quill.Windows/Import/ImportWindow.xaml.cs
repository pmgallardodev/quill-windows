using System.ComponentModel;
using Quill.Windows.UI;
using Quill.Windows.UI.Theme;
using Wpf.Ui.Controls;

namespace Quill.Windows.Import;

/// <summary>Modal "Importar grabación" dialog over <see cref="ImportViewModel"/>.</summary>
internal sealed partial class ImportWindow : FluentWindow
{
    private readonly ImportViewModel _viewModel;
    private bool _closeRequested;

    public ImportWindow(ImportViewModel viewModel)
    {
        InitializeComponent();
        ThemeBootstrapper.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    /// <summary>Closing with the title bar while converting cancels the conversion first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeRequested && _viewModel.IsImporting)
        {
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
            return;
        }

        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosing(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _closeRequested = true;
        Close();
    }
}
