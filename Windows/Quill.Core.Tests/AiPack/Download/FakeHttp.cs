using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Quill.Core.AiPack.Download;
using Quill.Core.Ports;

namespace Quill.Core.Tests.AiPack.Download;

/// <summary>Scripted HttpMessageHandler: no socket is ever opened by the download tests.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Partial(byte[] body, long from)
    {
        var slice = body[(int)from..];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
        return response;
    }

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    /// <summary>A 200 whose body is the given stream (no Content-Length when the stream cannot seek).</summary>
    public static HttpResponseMessage Streamed(Stream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) };
}

/// <summary>Readable stream that never declares its length, so size checks run on the bytes themselves.</summary>
internal sealed class UnknownLengthStream(byte[] data, Action<long>? onRead = null) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = (int)Math.Min(Math.Min(count, 4), data.Length - _position);
        Array.Copy(data, _position, buffer, offset, read);
        _position += read;
        onRead?.Invoke(_position);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class DownloadFixtures
{
    public const string LlmUrl = "https://huggingface.co/org/llm/resolve/0123abcd/model.gguf";
    public const string WhisperUrl = "https://huggingface.co/org/whisper/resolve/0123abcd/ggml.bin";

    public static readonly byte[] LlmBytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    public static readonly byte[] WhisperBytes = Enumerable.Range(0, 40).Select(i => (byte)(255 - i)).ToArray();

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static AiPackCatalogEntry Llm(byte[]? expected = null, long? size = null) => new(
        AiPackComponent.LanguageModel,
        "Test LLM",
        "llm/model.gguf",
        new Uri(LlmUrl),
        size ?? LlmBytes.Length,
        Sha(expected ?? LlmBytes));

    public static AiPackCatalogEntry Whisper() => new(
        AiPackComponent.LiveWhisper,
        "Test Whisper",
        "whisper/ggml.bin",
        new Uri(WhisperUrl),
        WhisperBytes.Length,
        Sha(WhisperBytes));

    public static AiPackDownloadPlan Plan(string root, params AiPackCatalogEntry[] entries) =>
        AiPackDownloadPlan.Create(new AiPackCatalog(entries), AiPackStatus.NotInstalled, root);
}
