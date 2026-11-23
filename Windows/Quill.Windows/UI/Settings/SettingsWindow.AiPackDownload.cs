using System.Windows;
using Quill.Core.AiPack.Download;
using Quill.Windows.AiPack;

namespace Quill.Windows.UI.Settings;

/// <summary>
/// "Descargar pack de IA" card of the IA page. The download starts only from its button; closing
/// the window cancels it and keeps the ".partial" files, so the next press resumes.
/// </summary>
internal partial class SettingsWindow
{
    private AiPackDownloadViewModel _aiPackDownload = null!;

    private async void InitializeAiPackDownload()
    {
        _aiPackDownload = new AiPackDownloadViewModel(
            new AiPackDownloadModule(SettingsModule.PersistAiPackPath),
            () => _viewModel.PackPath);
        _aiPackDownload.Ended += OnAiPackDownloadEnded;
        AiPackDownloadCard.DataContext = _aiPackDownload;
        await _aiPackDownload.RefreshAsync(_closing.Token);
    }

    private async void OnAiPackDownload(object sender, RoutedEventArgs e) =>
        await _aiPackDownload.StartAsync(_closing.Token);

    private void OnAiPackCancel(object sender, RoutedEventArgs e) => _aiPackDownload.Cancel();

    private async void OnAiPackCheckAgain(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshPackStatus();
        await _aiPackDownload.RefreshAsync(_closing.Token);
    }

    private void OnAiPackDownloadEnded(object? sender, AiPackDestination destination)
    {
        // A download into %LOCALAPPDATA% saved "ai.pack_path"; mirror it in the unsaved field so
        // "Guardar" does not clear it.
        if (destination.PersistAsPackPath && string.IsNullOrWhiteSpace(_viewModel.PackPath) &&
            File.Exists(Path.Combine(destination.Root, Quill.Core.AiPack.ManifestAiPackLocator.ManifestFileName)))
        {
            _viewModel.PackPath = destination.Root;
        }

        if (IsLoaded)
        {
            _viewModel.RefreshPackStatus();
        }
    }
}
