using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Quill.Core.Api;
using Quill.Core.Settings;
using Quill.Core.Storage;
using Quill.Windows.LocalApi;
using Xunit;

namespace Quill.Windows.Tests.LocalApi;

public sealed class LocalApiHostTests
{
    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false });

    [Fact]
    public async Task ServesOnlyWithTheTokenOverARealLoopbackSocket()
    {
        using var folder = new TempDirectory();
        var token = ApiToken.Generate();
        var port = LocalApiHost.PickFreePort();
        var handler = new ApiConnectionHandler(new LocalApiRouter(new LocalApiService(new FileSessionStore(folder.Path))), port, token);
        await using var host = LocalApiHost.Start(handler, port);
        using var client = Client();

        var anonymous = await client.GetAsync($"http://127.0.0.1:{port}/v1/sessions");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var authorized = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal("""{"sessions":[]}""", await authorized.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListensOnLoopbackOnly()
    {
        using var folder = new TempDirectory();
        var port = LocalApiHost.PickFreePort();
        var handler = new ApiConnectionHandler(new LocalApiRouter(new LocalApiService(new FileSessionStore(folder.Path))), port, ApiToken.Generate());
        await using var host = LocalApiHost.Start(handler, port);

        var external = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        if (external is null)
        {
            return; // No non-loopback IPv4 address on this machine.
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAnyAsync<SocketException>(() => socket.ConnectAsync(new IPEndPoint(external, port)));
    }
}

public sealed class WebhookDispatcherTests
{
    [Fact]
    public async Task PostsASignedEventToALoopbackReceiver()
    {
        var token = ApiToken.Generate();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var received = ReceiveOneAsync(receiver);
        await using var dispatcher = new WebhookDispatcher([$"http://127.0.0.1:{port}/hook"], token);
        var apiEvent = ApiEvent.Create(ApiEventTypes.SummaryCompleted, "s1", DateTimeOffset.Now);

        var status = await dispatcher.DeliverAsync($"http://127.0.0.1:{port}/hook", apiEvent, CancellationToken.None);
        var raw = await received;

        Assert.Equal(200, status);
        Assert.StartsWith("POST /hook HTTP/1.1", raw);
        var body = apiEvent.ToJson();
        Assert.Contains($"X-Quill-Signature: {WebhookSignature.Compute(token, Encoding.UTF8.GetBytes(body))}", raw);
        Assert.Contains("X-Quill-Event: summary.completed", raw);
        Assert.EndsWith(body, raw);
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("http://10.0.0.1/hook")]
    public async Task NeverSendsOffTheMachine(string url)
    {
        var handler = new CountingHandler();
        await using var dispatcher = new WebhookDispatcher([url], ApiToken.Generate(), handler);

        var status = await dispatcher.DeliverAsync(url, ApiEvent.Create(ApiEventTypes.SessionRecorded, "s1", DateTimeOffset.Now), CancellationToken.None);

        Assert.Null(status);
        Assert.Equal(0, handler.Calls);
        Assert.False(dispatcher.HasTargets);
    }

    [Fact]
    public async Task RetriesOnceOnServerErrors()
    {
        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        await using var dispatcher = new WebhookDispatcher(["http://127.0.0.1:9/hook"], ApiToken.Generate(), handler);

        var status = await dispatcher.DeliverAsync("http://127.0.0.1:9/hook", ApiEvent.Create(ApiEventTypes.SessionRecorded, "s1", DateTimeOffset.Now), CancellationToken.None);

        Assert.Equal(503, status);
        Assert.Equal(WebhookPolicy.MaxAttempts, handler.Calls);
    }

    [Fact]
    public async Task ConnectCallbackRejectsNonLoopbackHosts()
    {
        // 192.0.2.0/24 is TEST-NET-1: the callback must refuse before any packet is sent.
        using var handler = WebhookDispatcher.CreateLoopbackHandler();
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://192.0.2.1:9/"));
    }

    private static async Task<string> ReceiveOneAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var raw = text.ToString();
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split >= 0)
            {
                var lengthLine = raw[..split].Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = int.Parse(lengthLine.Split(':')[1].Trim());
                if (Encoding.UTF8.GetByteCount(raw[(split + 4)..]) >= length)
                {
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                    return raw;
                }
            }

            if (read == 0)
            {
                return raw;
            }
        }
    }

    private sealed class CountingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}

public sealed class LocalApiModuleTests
{
    [Fact]
    public async Task StaysOffWhenDisabled()
    {
        using var folder = new TempDirectory();
        await using var module = new LocalApiModule(new FileSessionStore(folder.Path), load: () => new ApiSettings(), save: _ => throw new InvalidOperationException());

        await module.StartAsync(CancellationToken.None);

        Assert.Equal(LocalApiState.Off, module.State);
        Assert.Null(module.Port);
    }

    [Fact]
    public async Task ProvisionsPersistsAndListensWhenEnabled()
    {
        using var folder = new TempDirectory();
        ApiSettings? saved = null;
        var port = LocalApiHost.PickFreePort();
        await using var module = new LocalApiModule(
            new FileSessionStore(folder.Path),
            load: () => saved ?? new ApiSettings { Enabled = true },
            save: settings => saved = settings,
            pickPort: () => port);

        await module.StartAsync(CancellationToken.None);

        Assert.Equal(LocalApiState.Listening, module.State);
        Assert.Equal(port, module.Port);
        Assert.NotNull(saved);
        Assert.True(saved!.IsProvisioned);

        await module.StopAsync();
        Assert.Equal(LocalApiState.Off, module.State);
    }

    [Fact]
    public async Task ReportsAPortInUse()
    {
        using var folder = new TempDirectory();
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var settings = new ApiSettings { Enabled = true, Port = port, Token = ApiToken.Generate() };
        await using var module = new LocalApiModule(new FileSessionStore(folder.Path), load: () => settings, save: _ => { });

        await module.StartAsync(CancellationToken.None);

        Assert.Equal(LocalApiState.Failed, module.State);
        Assert.NotNull(module.FailureReason);
    }
}
