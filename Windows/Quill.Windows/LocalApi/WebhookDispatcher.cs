using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Quill.Core.Api;

namespace Quill.Windows.LocalApi;

/// <summary>
/// POSTs <see cref="ApiEvent"/>s to the configured loopback webhooks, signed with
/// <see cref="WebhookSignature"/>. Nothing can leave the machine: every URL is re-validated with
/// <see cref="LoopbackUrl"/> at send time, the connection is opened by a callback that resolves the
/// host and connects only to loopback addresses, proxies and redirects are disabled. Delivery is
/// fire-and-forget through a bounded queue (oldest dropped), 3 s per attempt, at most 2 attempts.
/// </summary>
internal sealed class WebhookDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly IReadOnlyList<string> _urls;
    private readonly string _token;
    private readonly HttpClient _client;
    private readonly Channel<ApiEvent> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    public WebhookDispatcher(IReadOnlyList<string> urls, string token, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentException.ThrowIfNullOrEmpty(token);
        _urls = urls.Where(url => LoopbackUrl.TryParse(url, out _, out _)).ToList();
        _token = token;
        _client = new HttpClient(handler ?? CreateLoopbackHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _queue = Channel.CreateBounded<ApiEvent>(new BoundedChannelOptions(WebhookPolicy.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _pump = Task.Run(PumpAsync);
    }

    public bool HasTargets => _urls.Count > 0;

    /// <summary>Queues an event; returns false when there is nowhere to send it or the dispatcher stopped.</summary>
    public bool Enqueue(ApiEvent apiEvent) => HasTargets && _queue.Writer.TryWrite(apiEvent);

    /// <summary>Sends one event to one URL. Returns the last HTTP status, or null when nothing was answered.</summary>
    public async Task<int?> DeliverAsync(string url, ApiEvent apiEvent, CancellationToken cancellationToken)
    {
        if (!LoopbackUrl.TryParse(url, out var uri, out _))
        {
            return null;
        }

        var body = Encoding.UTF8.GetBytes(apiEvent.ToJson());
        var signature = WebhookSignature.Compute(_token, body);
        int? status = null;
        for (var attempt = 1; attempt <= WebhookPolicy.MaxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(WebhookPolicy.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Add(WebhookSignature.HeaderName, signature);
            request.Headers.Add("X-Quill-Event", apiEvent.Type);
            request.Headers.Add("X-Quill-Delivery", apiEvent.Id);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Quill", "1"));
            try
            {
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
            }
            catch (Exception exception) when (exception is HttpRequestException
                                                  || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                status = null;
            }

            if (!WebhookPolicy.ShouldRetry(attempt, status))
            {
                break;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pending deliveries are dropped on stop.
        }

        _client.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// No proxy, no redirects, no cookies, and a connect callback that refuses any address that is
    /// not loopback, whatever DNS or the hosts file say "localhost" is.
    /// </summary>
    internal static SocketsHttpHandler CreateLoopbackHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectTimeout = WebhookPolicy.Timeout,
        ConnectCallback = ConnectLoopbackOnlyAsync,
    };

    internal static async ValueTask<Stream> ConnectLoopbackOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var loopback = addresses.Where(LoopbackUrl.IsLoopback).ToList();
        if (loopback.Count == 0 || loopback.Count != addresses.Length)
        {
            throw new HttpRequestException($"webhook host \"{host}\" does not resolve only to loopback");
        }

        Exception? last = null;
        foreach (var address in loopback)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
        }

        throw new HttpRequestException("webhook connection failed", last);
    }

    private async Task PumpAsync()
    {
        await foreach (var apiEvent in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
        {
            foreach (var url in _urls)
            {
                var status = await DeliverAsync(url, apiEvent, _stopping.Token).ConfigureAwait(false);
                if (status is not (>= 200 and < 300))
                {
                    Debug.WriteLine($"webhook {apiEvent.Type} not delivered (status {status?.ToString() ?? "none"})");
                }
            }
        }
    }
}
