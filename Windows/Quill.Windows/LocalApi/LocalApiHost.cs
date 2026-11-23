using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Quill.Core.Api;

namespace Quill.Windows.LocalApi;

/// <summary>
/// Socket host of the local API. A <see cref="TcpListener"/> bound to 127.0.0.1 only (never
/// 0.0.0.0, never http.sys), so other machines cannot even open a connection, no URL ACL or admin
/// rights are needed and no firewall prompt appears. Exclusive address use stops another process
/// from binding the same port. One request per connection, at most <see cref="MaxConnections"/>
/// at a time, <see cref="RequestTimeout"/> per connection; the protocol work is done by
/// <see cref="ApiConnectionHandler"/>.
/// </summary>
/// <remarks>
/// Chosen over HttpListener (http.sys accepts on every interface and routes by Host; binding
/// 127.0.0.1 needs a URL ACL) and over Kestrel (pulls the ASP.NET Core shared framework into the
/// self-contained package for six endpoints).
/// </remarks>
internal sealed class LocalApiHost : IAsyncDisposable
{
    public const int MaxConnections = 8;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly ApiConnectionHandler _handler;
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    private LocalApiHost(TcpListener listener, ApiConnectionHandler handler)
    {
        _listener = listener;
        _handler = handler;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <exception cref="SocketException">The port is in use or cannot be bound.</exception>
    public static LocalApiHost Start(ApiConnectionHandler handler, int port)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start(backlog: MaxConnections * 2);
        return new LocalApiHost(listener, handler);
    }

    /// <summary>A port that is free on 127.0.0.1 right now (asks the OS for an ephemeral one).</summary>
    public static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                                  || (exception is SocketException && _stopping.IsCancellationRequested))
            {
                return;
            }
            catch (SocketException exception)
            {
                Debug.WriteLine($"local API accept failed: {exception.SocketErrorCode}");
                continue;
            }

            if (!_slots.Wait(0))
            {
                // Saturated: drop instead of queueing so a misbehaving client cannot pile up work.
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                timeout.CancelAfter(RequestTimeout);
                client.NoDelay = true;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                await using var stream = client.GetStream();
                await _handler.HandleAsync(stream, remote, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away or timed out; nothing to answer.
        }
        finally
        {
            _slots.Release();
        }
    }
}
