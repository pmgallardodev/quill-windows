using Quill.Core.Ports;

namespace Quill.Core.AiPack.Download;

/// <summary>
/// What a download would fetch into <see cref="Root"/>: the catalog entries the locator did
/// not find there. Bytes already on disk (a finished file or a ".partial" left by a cancelled
/// download) count as downloaded, so the free-space check only covers what is still missing.
/// </summary>
public sealed record AiPackDownloadPlan(
    string Root,
    IReadOnlyList<AiPackCatalogEntry> Items,
    long TotalBytes,
    long AlreadyDownloadedBytes)
{
    /// <summary>Extra free space asked for on top of the remaining bytes (10 %).</summary>
    public const double FreeSpaceMargin = 0.10;

    public const string PartialSuffix = ".partial";

    public bool IsEmpty => Items.Count == 0;

    public long RemainingBytes => Math.Max(0, TotalBytes - AlreadyDownloadedBytes);

    public long RequiredFreeBytes => RemainingBytes + (long)Math.Ceiling(RemainingBytes * FreeSpaceMargin);

    public bool HasEnoughSpace(long availableFreeBytes) => availableFreeBytes >= RequiredFreeBytes;

    /// <param name="status">Locator result; ignored unless it describes <paramref name="root"/>.</param>
    public static AiPackDownloadPlan Create(AiPackCatalog catalog, AiPackStatus status, string root)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var fullRoot = Path.GetFullPath(root);
        var sameRoot = status.Root is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(status.Root)),
            Path.TrimEndingDirectorySeparator(fullRoot),
            StringComparison.OrdinalIgnoreCase);

        // A split component whose parts are being joined in the background writes the same file:
        // leave it to the join instead of racing it.
        var items = catalog.Entries
            .Where(e => !(sameRoot && (status.Has(e.Component) ||
                status.PreparationOf(e.Component)?.State == ModelPreparationState.Preparing)))
            .ToList();
        // This synchronous compatibility path cannot authenticate a complete file. Be
        // conservative rather than treating an unverified same-size file as downloaded.
        return new AiPackDownloadPlan(fullRoot, items, items.Sum(i => i.Size), items.Sum(i => PartialBytesOnDisk(fullRoot, i)));
    }

    /// <summary>
    /// Creates a free-space plan after asynchronously verifying same-size final files against
    /// their pinned hashes. This lets a valid orphan be registered without requiring another
    /// model-sized free-space reserve, while keeping large-file hashing off the UI thread.
    /// </summary>
    public static async Task<AiPackDownloadPlan> CreateAsync(
        AiPackCatalog catalog,
        AiPackStatus status,
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        cancellationToken.ThrowIfCancellationRequested();

        var fullRoot = Path.GetFullPath(root);
        var items = ItemsToDownload(catalog, status, fullRoot);
        long alreadyDownloaded = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            alreadyDownloaded += await BytesOnDiskAsync(fullRoot, item, cancellationToken).ConfigureAwait(false);
        }

        return new AiPackDownloadPlan(fullRoot, items, items.Sum(i => i.Size), alreadyDownloaded);
    }

    public static string TargetPath(string root, AiPackCatalogEntry entry)
    {
        var full = Path.GetFullPath(Path.Combine(root, entry.Path));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new ArgumentException($"{entry.Component}: path escapes the pack folder");
    }

    private static List<AiPackCatalogEntry> ItemsToDownload(AiPackCatalog catalog, AiPackStatus status, string fullRoot)
    {
        var sameRoot = status.Root is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(status.Root)),
            Path.TrimEndingDirectorySeparator(fullRoot),
            StringComparison.OrdinalIgnoreCase);

        // A split component whose parts are being joined in the background writes the same file:
        // leave it to the join instead of racing it.
        return catalog.Entries
            .Where(e => !(sameRoot && (status.Has(e.Component) ||
                status.PreparationOf(e.Component)?.State == ModelPreparationState.Preparing)))
            .ToList();
    }

    private static async Task<long> BytesOnDiskAsync(string root, AiPackCatalogEntry entry, CancellationToken cancellationToken)
    {
        var target = TargetPath(root, entry);
        var complete = new FileInfo(target);
        if (complete.Exists && complete.Length == entry.Size)
        {
            var hash = await AiPackInstaller.HashFileAsync(target, cancellationToken).ConfigureAwait(false);
            if (string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Size;
            }
        }

        return PartialBytesOnDisk(root, entry);
    }

    private static long PartialBytesOnDisk(string root, AiPackCatalogEntry entry)
    {
        var target = TargetPath(root, entry);
        var partial = new FileInfo(target + PartialSuffix);
        return partial.Exists && partial.Length <= entry.Size ? partial.Length : 0;
    }
}
