using System.Runtime.InteropServices;
using System.Windows;
using Quill.Core.Settings;
using Quill.Windows.LocalApi;
using Quill.Windows.Resources;

namespace Quill.Windows.UI.Settings;

/// <summary>"API local" page of the Settings window. Its settings live in their own config.json
/// section and are saved through <see cref="ApiSettingsStore"/>, which notifies the running
/// <see cref="LocalApiModule"/> so it restarts with them.</summary>
internal partial class SettingsWindow
{
    private ApiSettingsViewModel _api = null!;

    private void InitializeApiPage()
    {
        _api = new ApiSettingsViewModel(ApiSettingsStore.Load());
        ApiPage.DataContext = _api;
    }

    private bool TryBuildApi(out ApiSettings settings)
    {
        if (_api.TryBuild(out settings, out var error))
        {
            return true;
        }

        _viewModel.Error = error;
        Navigation.SelectedItem = ApiNavigationItem;
        return false;
    }

    private void SaveApi(ApiSettings settings)
    {
        if (_api.HasChanges(settings))
        {
            ApiSettingsStore.Save(settings);
        }
    }

    private void OnCopyApiToken(object sender, RoutedEventArgs e)
    {
        if (!_api.HasToken)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_api.Token!);
            _api.Message = Loc.Get("Api_TokenCopied");
        }
        catch (COMException)
        {
            _api.Message = Loc.Get("Status_ClipboardBusy");
        }
    }

    private void OnRegenerateApiToken(object sender, RoutedEventArgs e) => _api.RegenerateToken();

    private void OnAddApiWebhook(object sender, RoutedEventArgs e) => _api.AddWebhook();

    private void OnRemoveApiWebhook(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            _api.RemoveWebhook(url);
        }
    }
}
