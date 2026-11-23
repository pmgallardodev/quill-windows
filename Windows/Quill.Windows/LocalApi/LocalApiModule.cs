using System.Net.Sockets;
using System.Reflection;
using Quill.Core.Api;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Quill.Windows.Infrastructure;

namespace Quill.Windows.LocalApi;

public enum LocalApiState
{
    /// <summary>Disabled in settings: nothing listens.</summary>
    Off,

    Listening,

    /// <summary>Enabled but could not start (port in use, config not writable...).</summary>
    Failed,
}

/// <summary>
/// Composition entry for the local API (F12). Owns the socket host and the webhook dispatcher and
/// rebuilds both from the "api" settings on <see cref="StartAsync"/> / <see cref="RestartAsync"/>.
/// Off by default: when <see cref="ApiSettings.Enabled"/> is false nothing is bound. On the first
/// enabled start it provisions and persists a token and a free port.
/// </summary>
/// <example>
/// var api = new LocalApiModule(store);              // optional: search: an IApiSearch from F10
/// await api.StartAsync(ct);                         // at app start (no-op when disabled)
/// ApiSettingsStore.Saved += async (_, _) => await api.RestartAsync(ct);
/// api.Publish(ApiEventTypes.SummaryCompleted, sessionId);
/// await api.DisposeAsync();                         // at exit
/// </example>
internal sealed class LocalApiModule : IAsyncDisposable
{
    private readonly ISessionStore _store;
    private readonly IApiSearch? _search;
    private readonly Func<ApiSettings> _load;
    private readonly Action<ApiSettings> _save;
    private readonly Func<int> _pickPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalApiHost? _host;
    private WebhookDispatcher? _webhooks;

    public LocalApiModule(
        ISessionStore store,
        IApiSearch? search = null,
        Func<ApiSettings>? load = null,
        Action<ApiSettings>? save = null,
        Func<int>? pickPort = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _search = search;
        _load = load ?? ApiSettingsStore.Load;
        _save = save ?? ApiSettingsStore.SaveWithoutNotifying;
        _pickPort = pickPort ?? LocalApiHost.PickFreePort;
    }

    public LocalApiState State { get; private set; } = LocalApiState.Off;

    /// <summary>Bound port while <see cref="LocalApiState.Listening"/>, otherwise null.</summary>
    public int? Port => _host?.Port;

    /// <summary>Why the last start failed (English, for logs), or null.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Raised (on a worker thread) after every state change.</summary>
    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            StartCore();
        }
        finally
        {
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads the settings and starts or stops accordingly (call after the settings were saved).</summary>
    public Task RestartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = LocalApiState.Off;
            FailureReason = null;
        }
        finally
        {
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sends an event to the webhooks. No-op while off or without webhooks; never blocks or throws.</summary>
    public void Publish(ApiEvent apiEvent)
    {
        ArgumentNullException.ThrowIfNull(apiEvent);
        _webhooks?.Enqueue(apiEvent);
    }

    /// <param name="type">One of <see cref="ApiEventTypes"/>.</param>
    public void Publish(string type, string sessionId) => Publish(ApiEvent.Create(type, sessionId, DateTimeOffset.Now));

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void StartCore()
    {
        var settings = _load();
        if (!settings.Enabled)
        {
            State = LocalApiState.Off;
            FailureReason = null;
            return;
        }

        try
        {
            var provisioned = settings.Provision(_pickPort);
            if (!ReferenceEquals(provisioned, settings))
            {
                _save(provisioned);
            }

            var service = new LocalApiService(_store, _search);
            var mcp = provisioned.McpEnabled ? new McpServer(service, Version) : null;
            var handler = new ApiConnectionHandler(new LocalApiRouter(service, mcp), provisioned.Port, provisioned.Token!);
            _host = LocalApiHost.Start(handler, provisioned.Port);
            _webhooks = provisioned.Webhooks.Count > 0 ? new WebhookDispatcher(provisioned.Webhooks, provisioned.Token!) : null;
            State = LocalApiState.Listening;
            FailureReason = null;
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException
                                              or FormatException or InvalidOperationException)
        {
            State = LocalApiState.Failed;
            FailureReason = exception is SocketException socket ? $"socket error {socket.SocketErrorCode}" : exception.GetType().Name;
        }
    }

    private async Task StopCoreAsync()
    {
        if (_host is { } host)
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }

        if (_webhooks is { } webhooks)
        {
            _webhooks = null;
            await webhooks.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Version =>
        typeof(LocalApiModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

/// <summary>Reads and writes the "api" settings through <see cref="ConfigStore"/> and announces saves.</summary>
internal static class ApiSettingsStore
{
    /// <summary>Raised after the user saved API settings (Settings window). Wire it to <see cref="LocalApiModule.RestartAsync"/>.</summary>
    public static event EventHandler<ApiSettings>? Saved;

    public static ApiSettings Load() => ConfigStore.LoadApiSettings();

    public static void Save(ApiSettings settings)
    {
        ConfigStore.SaveApiSettings(settings);
        Saved?.Invoke(null, settings);
    }

    /// <summary>Persists provisioning (token/port picked at start) without triggering a restart loop.</summary>
    public static void SaveWithoutNotifying(ApiSettings settings) => ConfigStore.SaveApiSettings(settings);
}
