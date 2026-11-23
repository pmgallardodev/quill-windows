using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Quill.Core.Settings;
using Quill.Windows.Resources;
using Quill.Windows.UI.Theme;

namespace Quill.Windows.UI.Settings;

/// <summary>
/// Settings window: General, IA, Resúmenes, Plantillas, Exportación, Privacidad. Owns the dialogs and
/// shell calls; state and validation live in <see cref="SettingsViewModel"/>.
/// </summary>
internal partial class SettingsWindow
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action<QuillSettings> _save;
    private readonly CancellationTokenSource _closing = new();

    public SettingsWindow(SettingsViewModel viewModel, Action<QuillSettings> save)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        InitializeComponent();
        DataContext = _viewModel;
        InitializeApiPage();
        InitializeAiPackDownload();
        ThemeBootstrapper.Attach(this);
        Closed += (_, _) => _closing.Cancel();
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires during InitializeComponent, before the pages exist.
        if (ApiPage is null)
        {
            return;
        }

        UIElement[] pages = [GeneralPage, AiPage, SummariesPage, TemplatesPage, ExportPage, PrivacyPage, ApiPage];
        for (var index = 0; index < pages.Length; index++)
        {
            pages[index].Visibility = index == Navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.GetDirectoryName(_viewModel.ConfigPath));

    private async void OnBrowsePackPath(object sender, RoutedEventArgs e)
    {
        if (PickFolder(_viewModel.PackPath) is { } folder)
        {
            _viewModel.PackPath = folder;
            _viewModel.RefreshPackStatus();
            await _aiPackDownload.RefreshAsync(_closing.Token);
        }
    }

    private async void OnCheckPack(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshPackStatus();
        await _aiPackDownload.RefreshAsync(_closing.Token);
    }

    private void OnBrowseLlmModel(object sender, RoutedEventArgs e)
    {
        if (PickFile(_viewModel.LanguageModelPath, "GGUF (*.gguf)|*.gguf") is { } file)
        {
            _viewModel.LanguageModelPath = file;
        }
    }

    private void OnBrowseLiveWhisperModel(object sender, RoutedEventArgs e)
    {
        if (PickFile(_viewModel.LiveWhisperModelPath, "Whisper (*.bin)|*.bin") is { } file)
        {
            _viewModel.LiveWhisperModelPath = file;
        }
    }

    private void OnBrowseTemplatesFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder(_viewModel.ResolvedTemplatesFolder) is { } folder)
        {
            _viewModel.TemplatesFolder = folder;
            _viewModel.ReloadTemplates();
        }
    }

    private void OnOpenTemplatesFolder(object sender, RoutedEventArgs e)
    {
        var folder = _viewModel.ResolvedTemplatesFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _viewModel.Error = Loc.Format("Settings_Error_Open", folder, exception.Message);
            return;
        }

        OpenFolder(folder);
    }

    private void OnReloadTemplates(object sender, RoutedEventArgs e) => _viewModel.ReloadTemplates();

    private void OnCustomizeTemplate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.CustomizeSelected() is { } path)
            {
                Open(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.Error = Loc.Format("Settings_Error_Open", _viewModel.ResolvedTemplatesFolder, exception.Message);
        }
    }

    private void OnBrowseExportFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder(_viewModel.ExportFolder) is { } folder)
        {
            _viewModel.ExportFolder = folder;
        }
    }

    private async void OnExportAll(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ExportAllAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closed while exporting.
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryBuild(out var settings) || !TryBuildApi(out var api))
        {
            return;
        }

        try
        {
            _save(settings);
            SaveApi(api);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            _viewModel.Error = Loc.Format("Settings_Error_Save", exception.Message);
            return;
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private string? PickFolder(string? initial)
    {
        var dialog = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickFile(string? initial, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        var directory = string.IsNullOrWhiteSpace(initial) ? null : Path.GetDirectoryName(initial);
        if (directory is not null && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OpenFolder(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Open(folder);
        }
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _viewModel.Error = Loc.Format("Settings_Error_Open", path, exception.Message);
        }
    }
}
