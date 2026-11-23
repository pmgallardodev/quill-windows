using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Quill.Core.Api;
using Quill.Core.Settings;
using Quill.Windows.Resources;

namespace Quill.Windows.UI.Settings;

/// <summary>
/// State of the "API local" settings page. Validates webhooks as they are added and the port on
/// save; generates the token when the API is turned on without one. No WPF types: clipboard and
/// navigation stay in <see cref="SettingsWindow"/>.
/// </summary>
internal sealed class ApiSettingsViewModel : INotifyPropertyChanged
{
    private readonly ApiSettings _original;
    private bool _enabled;
    private string _portText;
    private string? _token;
    private bool _showToken;
    private bool _mcpEnabled;
    private string _newWebhook = string.Empty;
    private string? _webhookError;
    private string? _message;

    public ApiSettingsViewModel(ApiSettings settings)
    {
        _original = settings ?? throw new ArgumentNullException(nameof(settings));
        _enabled = settings.Enabled;
        _portText = ApiSettings.IsValidPort(settings.Port) ? settings.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _token = settings.Token;
        _mcpEnabled = settings.McpEnabled;
        Webhooks = new ObservableCollection<string>(settings.Webhooks);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value) && value && !ApiToken.IsWellFormed(_token))
            {
                Token = ApiToken.Generate();
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (Set(ref _portText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(EndpointText));
            }
        }
    }

    /// <summary>Where the API answers, or a note that a free port is picked on first start.</summary>
    public string EndpointText => TryPort(out var port) && port != 0
        ? $"http://127.0.0.1:{port}/v1/"
        : Loc.Get("Api_EndpointPending");

    public string? Token
    {
        get => _token;
        private set
        {
            if (Set(ref _token, value))
            {
                OnPropertyChanged(nameof(TokenDisplay));
                OnPropertyChanged(nameof(HasToken));
            }
        }
    }

    public bool HasToken => ApiToken.IsWellFormed(_token);

    public bool ShowToken
    {
        get => _showToken;
        set
        {
            if (Set(ref _showToken, value))
            {
                OnPropertyChanged(nameof(TokenDisplay));
            }
        }
    }

    /// <summary>Masked unless <see cref="ShowToken"/>: only the last four characters are visible.</summary>
    public string TokenDisplay => !HasToken
        ? Loc.Get("Api_TokenPending")
        : _showToken ? _token! : new string('•', 12) + _token![^4..];

    public bool McpEnabled
    {
        get => _mcpEnabled;
        set => Set(ref _mcpEnabled, value);
    }

    public ObservableCollection<string> Webhooks { get; }

    public string NewWebhook
    {
        get => _newWebhook;
        set => Set(ref _newWebhook, value ?? string.Empty);
    }

    public string? WebhookError
    {
        get => _webhookError;
        private set
        {
            if (Set(ref _webhookError, value))
            {
                OnPropertyChanged(nameof(HasWebhookError));
            }
        }
    }

    public bool HasWebhookError => !string.IsNullOrEmpty(_webhookError);

    /// <summary>Feedback for copy/regenerate (shown under the token).</summary>
    public string? Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public bool HasChanges(ApiSettings built) =>
        built.Enabled != _original.Enabled || built.Port != _original.Port || built.Token != _original.Token
        || built.McpEnabled != _original.McpEnabled || !built.Webhooks.SequenceEqual(_original.Webhooks, StringComparer.Ordinal);

    public void RegenerateToken()
    {
        Token = ApiToken.Generate();
        Message = Loc.Get("Api_RegenerateHint");
    }

    public bool AddWebhook()
    {
        if (!LoopbackUrl.TryParse(_newWebhook, out _, out var error))
        {
            WebhookError = Loc.Get("Api_Error_Webhook" + error);
            return false;
        }

        var url = _newWebhook.Trim();
        if (Webhooks.Contains(url, StringComparer.Ordinal))
        {
            WebhookError = Loc.Get("Api_Error_WebhookDuplicate");
            return false;
        }

        if (Webhooks.Count >= ApiSettings.MaxWebhooks)
        {
            WebhookError = Loc.Format("Api_Error_WebhookLimit", ApiSettings.MaxWebhooks);
            return false;
        }

        Webhooks.Add(url);
        NewWebhook = string.Empty;
        WebhookError = null;
        return true;
    }

    public void RemoveWebhook(string url) => Webhooks.Remove(url);

    /// <summary>Builds the settings, or returns false with a user-facing <paramref name="error"/>.</summary>
    public bool TryBuild(out ApiSettings settings, out string? error)
    {
        settings = _original;
        if (!TryPort(out var port))
        {
            error = Loc.Get("Api_Error_Port");
            return false;
        }

        if (_enabled && !HasToken)
        {
            Token = ApiToken.Generate();
        }

        error = null;
        settings = new ApiSettings
        {
            Enabled = _enabled,
            Port = port,
            Token = HasToken ? _token : null,
            Webhooks = Webhooks.ToList(),
            McpEnabled = _mcpEnabled,
        };
        return true;
    }

    private bool TryPort(out int port)
    {
        port = 0;
        var text = _portText.Trim();
        return text.Length == 0
            || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && ApiSettings.IsValidPort(port));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
