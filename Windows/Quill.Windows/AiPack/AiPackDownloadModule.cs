using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using Quill.Core.AiPack;
using Quill.Core.AiPack.Download;

namespace Quill.Windows.AiPack;

/// <summary>What a press of "Descargar pack de IA" would do, computed locally (no network).</summary>
/// <param name="FreeBytes">Free space on the destination drive; null when it cannot be read.</param>
internal sealed record AiPackDownloadOffer(AiPackDestination Destination, AiPackDownloadPlan Plan, long? FreeBytes)
{
    public bool HasEnoughSpace => FreeBytes is not { } free || Plan.HasEnoughSpace(free);
}

/// <summary>Port used by the Settings page, so its view model is testable without a network.</summary>
internal interface IAiPackDownloader
{
    /// <param name="packPath">"ai.pack_path" as typed in Settings (may be unsaved or empty).</param>
    Task<AiPackDownloadOffer> PrepareAsync(string? packPath, CancellationToken cancellationToken);

    /// <summary>Downloads the offer's plan. Throws <see cref="AiPackDownloadException"/>,
    /// <see cref="HttpRequestException"/>, <see cref="IOException"/> or
    /// <see cref="OperationCanceledException"/>; finished components stay installed.</summary>
    Task DownloadAsync(AiPackDownloadOffer offer, IProgress<AiPackDownloadProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The in-app AI-pack download (P2). It runs only when the user presses the button, talks only to the hosts in
/// <see cref="AiPackHostPolicy"/>, and its HttpClient is created on the first download, never at
/// startup. One HttpClient for the process: SocketsHttpHandler with automatic redirects off (the
/// installer validates every hop), TLS 1.2/1.3, no cookies, a connect timeout, and the plain
/// "Quill" User-Agent with no version, machine or user data.
/// </summary>
internal sealed class AiPackDownloadModule(Action<string> persistPackPath) : IAiPackDownloader
{
    private static readonly Lazy<HttpClient> Client = new(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Action<string> _persistPackPath = persistPackPath ?? throw new ArgumentNullException(nameof(persistPackPath));

    /// <summary>Raised after an install leaves a manifest on disk, including the default destination.</summary>
    internal static event EventHandler? PackAvailabilityChanged;

    public async Task<AiPackDownloadOffer> PrepareAsync(string? packPath, CancellationToken cancellationToken)
    {
        var configured = string.IsNullOrWhiteSpace(packPath) ? null : Environment.ExpandEnvironmentVariables(packPath);
        var destination = AiPackDestination.Resolve(
            configured,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            IsWritable);
        var status = new ManifestAiPackLocator(AppContext.BaseDirectory, destination.Root).Locate();
        var plan = await AiPackDownloadPlan.CreateAsync(AiPackCatalog.Default, status, destination.Root, cancellationToken).ConfigureAwait(false);
        return new AiPackDownloadOffer(destination, plan, FreeSpace(destination.Root));
    }

    public async Task DownloadAsync(AiPackDownloadOffer offer, IProgress<AiPackDownloadProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Directory.CreateDirectory(offer.Destination.Root);
        try
        {
            var installer = new AiPackInstaller(Client.Value);
            await installer.InstallAsync(offer.Plan, progress, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            // Save "ai.pack_path" as soon as the folder holds a manifest (even after a partial
            // run), so the locator finds the components that did finish.
            if (offer.Destination.PersistAsPackPath &&
                File.Exists(Path.Combine(offer.Destination.Root, ManifestAiPackLocator.ManifestFileName)))
            {
                _persistPackPath(offer.Destination.Root);
            }

            if (File.Exists(Path.Combine(offer.Destination.Root, ManifestAiPackLocator.ManifestFileName)))
            {
                PackAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };

        // No whole-request timeout: a 3 GB download can take hours. The installer cancels a
        // request or read that stalls (no data for 60 s) instead.
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>True when Quill can create files in <paramref name="folder"/> (probed in the folder,
    /// or in its parent when it does not exist yet, so nothing is created just by asking).</summary>
    private static bool IsWritable(string folder)
    {
        var probeFolder = Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder);
        if (string.IsNullOrEmpty(probeFolder) || !Directory.Exists(probeFolder))
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(probeFolder, $".quill-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static long? FreeSpace(string root)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            return string.IsNullOrEmpty(drive) ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
