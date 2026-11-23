using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Quill.Core.AiPack.Download;

/// <summary>
/// Downloads the files of an <see cref="AiPackDownloadPlan"/> and registers them in ai-pack.json.
/// Only ever started by the user ("Descargar pack de IA"); nothing calls it at startup.
/// </summary>
/// <remarks>
/// Per file: every URL and redirect hop must pass <see cref="AiPackHostPolicy"/> (HTTPS, allowlisted
/// host) and redirects are followed here, never by the handler, so the caller's HttpClient must have
/// AllowAutoRedirect off. The body streams to "&lt;file&gt;.partial", resuming with an HTTP Range when
/// the server answers 206 for the expected offset (a 200 restarts from zero). The pinned size is a
/// hard cap: a larger declared or received body aborts and deletes the partial file. The SHA-256 is
/// computed while writing and checked before the atomic rename; a mismatch deletes the file.
/// Cancellation, a stall or a dropped connection keep the partial file for the next run.
/// </remarks>
public sealed class AiPackInstaller
{
    public const int MaxRedirects = 5;
    public const string UserAgent = "Quill";

    private const int BufferSize = 256 * 1024;
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly AiPackHostPolicy _policy;
    private readonly TimeSpan _stallTimeout;

    public AiPackInstaller(HttpClient http, AiPackHostPolicy? policy = null, TimeSpan? stallTimeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _policy = policy ?? AiPackHostPolicy.Default;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
    }

    public async Task InstallAsync(
        AiPackDownloadPlan plan,
        IProgress<AiPackDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var tracker = new Tracker(plan.TotalBytes, progress);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InstallItemAsync(plan.Root, item, tracker, cancellationToken).ConfigureAwait(false);

            // Registered one by one, so a later failure still leaves the finished models usable.
            AiPackManifestWriter.Register(plan.Root, [item]);
            tracker.Completed(item);
        }
    }

    private async Task InstallItemAsync(string root, AiPackCatalogEntry item, Tracker tracker, CancellationToken cancellationToken)
    {
        var target = AiPackDownloadPlan.TargetPath(root, item);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length == item.Size)
        {
            tracker.Verifying(item);
            if (await HashFileAsync(target, cancellationToken).ConfigureAwait(false) == item.Sha256)
            {
                return;
            }
        }

        var partial = target + AiPackDownloadPlan.PartialSuffix;
        var hash = await DownloadAsync(item, partial, tracker, cancellationToken).ConfigureAwait(false);
        if (hash != item.Sha256)
        {
            File.Delete(partial);
            throw new AiPackDownloadException(
                AiPackDownloadFailure.HashMismatch,
                item.Component,
                $"{item.Name}: SHA-256 {hash} does not match the pinned {item.Sha256}");
        }

        File.Move(partial, target, overwrite: true);
    }

    /// <summary>Brings "&lt;file&gt;.partial" to exactly the pinned size; returns its SHA-256.</summary>
    private async Task<string> DownloadAsync(AiPackCatalogEntry item, string partial, Tracker tracker, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > item.Size)
        {
            File.Delete(partial);
            offset = 0;
        }

        if (offset == item.Size)
        {
            tracker.Verifying(item);
            return await HashFileAsync(partial, cancellationToken).ConfigureAwait(false);
        }

        using var response = await SendAsync(item, offset, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            offset = 0;
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent && offset > 0)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || (range.Length is { } length && length != item.Size))
            {
                throw Mismatch(item, partial, $"{item.Name}: the server resumed at an unexpected range ({range})");
            }
        }
        else
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partial);
            }

            throw new AiPackDownloadException(
                AiPackDownloadFailure.HttpStatus,
                item.Component,
                $"{item.Name}: HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } declared && offset + declared != item.Size)
        {
            throw Mismatch(item, partial, $"{item.Name}: the server announced {offset + declared} bytes, expected {item.Size}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (offset > 0)
        {
            await AppendFileAsync(hash, partial, cancellationToken).ConfigureAwait(false);
        }

        var received = offset;
        var buffer = new byte[BufferSize];
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = new FileStream(
            partial,
            offset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true))
        {
            tracker.Downloading(item, received, force: true);
            while (true)
            {
                var read = await ReadWithStallTimeoutAsync(body, buffer, item, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (received + read > item.Size)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    throw Mismatch(item, partial, $"{item.Name}: the server sent more than the pinned {item.Size} bytes");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                tracker.Downloading(item, received, force: false);
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (received < item.Size)
        {
            throw new AiPackDownloadException(
                AiPackDownloadFailure.Incomplete,
                item.Component,
                $"{item.Name}: the connection ended at {received} of {item.Size} bytes");
        }

        tracker.Verifying(item);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>GET with manual, validated redirects. The Range header is repeated on every hop.</summary>
    private async Task<HttpResponseMessage> SendAsync(AiPackCatalogEntry item, long offset, CancellationToken cancellationToken)
    {
        var current = item.Url;
        EnsureAllowed(item, current);
        for (var hop = 0; ; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue(UserAgent)));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            HttpResponseMessage response;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                stall.CancelAfter(_stallTimeout);
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Stalled(item, exception);
                }
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            var status = (int)response.StatusCode;
            response.Dispose();
            if (location is null)
            {
                throw new AiPackDownloadException(AiPackDownloadFailure.HttpStatus, item.Component, $"{item.Name}: redirect without Location", status);
            }

            if (hop >= MaxRedirects)
            {
                throw new AiPackDownloadException(AiPackDownloadFailure.TooManyRedirects, item.Component, $"{item.Name}: more than {MaxRedirects} redirects");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            EnsureAllowed(item, current);
        }
    }

    private async Task<int> ReadWithStallTimeoutAsync(Stream body, byte[] buffer, AiPackCatalogEntry item, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            return await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Stalled(item, exception);
        }
    }

    private void EnsureAllowed(AiPackCatalogEntry item, Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new AiPackDownloadException(AiPackDownloadFailure.InsecureUrl, item.Component, $"{item.Name}: refused non-HTTPS URL {url.GetLeftPart(UriPartial.Authority)}");
        }

        if (!_policy.IsAllowed(url))
        {
            throw new AiPackDownloadException(AiPackDownloadFailure.HostNotAllowed, item.Component, $"{item.Name}: refused host {url.Host}");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static AiPackDownloadException Mismatch(AiPackCatalogEntry item, string partial, string message)
    {
        File.Delete(partial);
        return new AiPackDownloadException(AiPackDownloadFailure.SizeMismatch, item.Component, message);
    }

    private static AiPackDownloadException Stalled(AiPackCatalogEntry item, Exception inner) =>
        new(AiPackDownloadFailure.Stalled, item.Component, $"{item.Name}: no data received in time", inner: inner);

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task AppendFileAsync(IncrementalHash hash, string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    /// <summary>Turns per-file byte counts into throttled plan-wide progress reports.</summary>
    private sealed class Tracker(long totalBytes, IProgress<AiPackDownloadProgress>? progress)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _completedBytes;
        private long _sessionBytes;
        private long _lastItemBytes = -1;
        private TimeSpan _lastReport = TimeSpan.MinValue;

        public void Downloading(AiPackCatalogEntry item, long itemBytes, bool force)
        {
            if (_lastItemBytes >= 0 && itemBytes > _lastItemBytes)
            {
                _sessionBytes += itemBytes - _lastItemBytes;
            }

            _lastItemBytes = itemBytes;
            var now = _clock.Elapsed;
            if (force || now - _lastReport >= ReportInterval)
            {
                _lastReport = now;
                Report(item, AiPackDownloadPhase.Downloading, itemBytes);
            }
        }

        public void Verifying(AiPackCatalogEntry item) => Report(item, AiPackDownloadPhase.Verifying, item.Size);

        public void Completed(AiPackCatalogEntry item)
        {
            _completedBytes += item.Size;
            _lastItemBytes = -1;
            Report(item, AiPackDownloadPhase.Completed, item.Size, countItem: false);
        }

        private void Report(AiPackCatalogEntry item, AiPackDownloadPhase phase, long itemBytes, bool countItem = true)
        {
            if (progress is null)
            {
                return;
            }

            var seconds = _clock.Elapsed.TotalSeconds;
            progress.Report(new AiPackDownloadProgress(
                item.Component,
                phase,
                itemBytes,
                item.Size,
                _completedBytes + (countItem ? itemBytes : 0),
                totalBytes,
                seconds > 0 ? _sessionBytes / seconds : 0));
        }
    }
}
