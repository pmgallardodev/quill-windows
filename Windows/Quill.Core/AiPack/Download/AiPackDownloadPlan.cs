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
        return new AiPackDownloadPlan(fullRoot, items, items.Sum(i => i.Size), items.Sum(i => BytesOnDisk(fullRoot, i)));
    }

    public static string TargetPath(string root, AiPackCatalogEntry entry)
    {
        var full = Path.GetFullPath(Path.Combine(root, entry.Path));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new ArgumentException($"{entry.Component}: path escapes the pack folder");
    }

    private static long BytesOnDisk(string root, AiPackCatalogEntry entry)
    {
        var target = TargetPath(root, entry);
        var complete = new FileInfo(target);
        if (complete.Exists && complete.Length == entry.Size)
        {
            return entry.Size;
        }

        var partial = new FileInfo(target + PartialSuffix);
        return partial.Exists && partial.Length <= entry.Size ? partial.Length : 0;
    }
}
