using Quill.Core.Export;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Quill.Core.Templates;
using Quill.Windows.Infrastructure;

namespace Quill.Windows.UI.Settings;

/// <summary>
/// Composition entry for the Settings window and the services that read its settings.
/// Call <see cref="Show"/> from the tray menu (UI thread). One window at a time: a second call
/// activates the open one. Settings are read from and written to config.json through
/// <see cref="ConfigStore"/>; most of them take effect when the consumers are next created
/// (see <see cref="Saved"/>).
/// </summary>
internal static class SettingsModule
{
    private static SettingsWindow? _window;

    /// <summary>Raised on the UI thread after the user saved new settings.</summary>
    public static event EventHandler<QuillSettings>? Saved;

    /// <summary>Current settings (lenient: defaults when config.json is missing or broken).</summary>
    public static QuillSettings Current => ConfigStore.LoadSettings();

    /// <param name="store">Session store for "Exportar todo ahora"; when null the button stays disabled.</param>
    public static void Show(ISessionStore? store = null)
    {
        if (_window is not null)
        {
            if (_window.WindowState == System.Windows.WindowState.Minimized)
            {
                _window.WindowState = System.Windows.WindowState.Normal;
            }

            _window.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(
            ConfigStore.LoadSettings(),
            ConfigStore.CreateAiPackLocator,
            ConfigStore.TemplatesFolder,
            store is null ? null : new MeetingExportService(store),
            ConfigStore.ConfigPath,
            RecordingsFolderOrEmpty());
        _window = new SettingsWindow(viewModel, Save);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    /// <summary>Templates catalog for the summary use cases: built-ins merged with the user folder.</summary>
    public static TemplateCatalog CreateTemplateCatalog(QuillSettings? settings = null) =>
        new(ConfigStore.TemplatesFolder(settings ?? Current));

    /// <summary>Exports a meeting after its final summary when "auto_export" is on; null otherwise.</summary>
    public static Task<MarkdownExportResult?> AutoExportAsync(ISessionStore store, string sessionId, CancellationToken cancellationToken) =>
        new MeetingExportService(store).ExportIfEnabledAsync(sessionId, Current.Export, cancellationToken);

    /// <summary>Saves "ai.pack_path" after an in-app AI-pack download outside the app folder and
    /// notifies <see cref="Saved"/> so running services locate the new pack. UI thread.</summary>
    internal static void PersistAiPackPath(string packRoot) =>
        Saved?.Invoke(null, ConfigStore.SaveAiPackPath(packRoot));

    private static void Save(QuillSettings settings)
    {
        ConfigStore.SaveSettings(settings);
        Saved?.Invoke(null, settings);
    }

    private static string RecordingsFolderOrEmpty()
    {
        try
        {
            return ConfigStore.ResolveRecordingsRoot(null);
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
