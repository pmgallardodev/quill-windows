using System.Security.Cryptography;
using System.Collections.Concurrent;
using Quill.Core.Ports;

namespace Quill.Core.AiPack;

/// <summary>
/// How a model too large for one GitHub release asset (2 GiB) is cut into byte ranges.
/// tools/build-ai-pack.ps1 mirrors <see cref="PartSizes"/> and <see cref="PartPath"/>.
/// </summary>
public static class SplitModelPlan
{
    /// <summary>1.9 GiB: leaves room under the 2 GiB asset limit for the ZIP headers.</summary>
    public const long DefaultMaxPartBytes = 2_040_109_465;

    /// <summary>
    /// The fewest parts of at most <paramref name="maxPartBytes"/>, balanced so they differ by at
    /// most one byte; the larger parts come first.
    /// </summary>
    public static IReadOnlyList<long> PartSizes(long totalBytes, long maxPartBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartBytes);
        var count = (totalBytes + maxPartBytes - 1) / maxPartBytes;
        var size = totalBytes / count;
        var remainder = totalBytes % count;
        var sizes = new long[count];
        for (var index = 0; index < count; index++)
        {
            sizes[index] = size + (index < remainder ? 1 : 0);
        }

        return sizes;
    }

    /// <summary>"llm/model.gguf", 2 becomes "llm/model.gguf.part2".</summary>
    public static string PartPath(string filePath, int partNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partNumber);
        return $"{filePath}.part{partNumber}";
    }
}

/// <summary>One byte range of a split model; <paramref name="Sha256"/> is optional lowercase hex.</summary>
public sealed record SplitModelPart(string Path, long Size, string? Sha256);

/// <summary>
/// A split model to join. <paramref name="TargetPath"/> is the file the manifest names inside the
/// pack; <paramref name="FallbackPath"/> is used when the pack folder cannot be written.
/// <paramref name="Sha256"/> is the lowercase hex hash of the whole file.
/// </summary>
public sealed record SplitModelJob(
    AiPackComponent Component,
    string TargetPath,
    string FallbackPath,
    long Size,
    string Sha256,
    IReadOnlyList<SplitModelPart> Parts);

public enum SplitAssemblyOutcome
{
    Assembled,
    PartsMissing,
    PartCorrupt,
    HashMismatch,
    NotEnoughSpace,
    NotWritable,
}

/// <summary>Result of <see cref="SplitModelAssembler.AssembleAsync"/>; <see cref="Path"/> is set when assembled.</summary>
public sealed record SplitAssemblyResult(SplitAssemblyOutcome Outcome, string? Path, string? Error)
{
    public bool Succeeded => Outcome == SplitAssemblyOutcome.Assembled;

    /// <summary>False when the parts themselves are bad: joining them again cannot succeed.</summary>
    public bool Retryable => Outcome is SplitAssemblyOutcome.NotEnoughSpace or SplitAssemblyOutcome.NotWritable or SplitAssemblyOutcome.PartsMissing;
}

/// <summary>
/// Joins the parts of a split model into one file: checks every part's size and the free space
/// first, streams the parts into "&lt;target&gt;.joining" while hashing them, verifies each part's
/// and the whole file's SHA-256, renames the temporary file into place (atomic on one volume) and
/// only then deletes the parts. A leftover ".joining" file from an interrupted run is overwritten.
/// When the pack folder cannot be written (or lacks space) the file goes to
/// <see cref="SplitModelJob.FallbackPath"/> and the parts stay where they are.
/// </summary>
public sealed class SplitModelAssembler
{
    /// <summary>Spare bytes required on top of the model size before joining.</summary>
    public const long FreeSpaceMarginBytes = 64L * 1024 * 1024;

    private readonly Func<string, long?> _freeSpace;
    private readonly int _bufferSize;
    private static readonly ConcurrentDictionary<string, byte> ActiveStaging = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="freeSpace">Free bytes on the volume of a folder, or null when unknown (then joining is attempted).</param>
    public SplitModelAssembler(Func<string, long?>? freeSpace = null, int bufferSize = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 16);
        _freeSpace = freeSpace ?? FreeSpaceOf;
        _bufferSize = bufferSize;
    }

    public static string TemporaryPath(string targetPath) => targetPath + ".joining";

    /// <param name="progress">Receives the number of bytes joined so far.</param>
    public async Task<SplitAssemblyResult> AssembleAsync(
        SplitModelJob job,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var missing = job.Parts.FirstOrDefault(part => LengthOf(part.Path) != part.Size);
        if (job.Parts.Count == 0 || missing is not null)
        {
            return new SplitAssemblyResult(
                SplitAssemblyOutcome.PartsMissing,
                null,
                missing is null ? "the manifest lists no parts" : $"part {System.IO.Path.GetFileName(missing.Path)} is missing or has the wrong size");
        }

        var lackedSpace = false;
        foreach (var target in new[] { job.TargetPath, job.FallbackPath })
        {
            var folder = System.IO.Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(folder))
            {
                continue;
            }

            var temporary = TemporaryPath(target);
            if (!ActiveStaging.TryAdd(temporary, 0))
            {
                // Another join in this process owns the reservation. Do not truncate its
                // preallocated output; the fallback target may still be available.
                continue;
            }

            FileStream output;
            try
            {
                Directory.CreateDirectory(folder);
                // Open-or-create while holding an exclusive handle. A stale reservation from an
                // interrupted process is safely reused after this succeeds; an active join keeps
                // its handle open and therefore cannot be truncated by a concurrent join.
                output = new FileStream(temporary, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, _bufferSize, FileOptions.Asynchronous);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ActiveStaging.TryRemove(temporary, out _);
                continue;
            }

            try
            {
                // Reclaim an interrupted reservation before checking free space. Checking the
                // logical length first can reject a retry even though truncating the stale file
                // has released its real allocation. The exclusive handle remains held throughout.
                output.SetLength(0);
                output.Flush(flushToDisk: true);
                var free = SafeFreeSpace(folder);
                if (free is { } bytes && bytes < job.Size + FreeSpaceMarginBytes)
                {
                    lackedSpace = true;
                    output.Dispose();
                    TryDelete(temporary);
                    continue;
                }

                SplitAssemblyResult? failure;
                try
                {
                    output.SetLength(job.Size);
                    output.Position = 0;
                    await using (output.ConfigureAwait(false))
                    {
                        failure = await CopyPartsAsync(job, output, progress, cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                }
                catch
                {
                    TryDelete(temporary);
                    throw;
                }

                if (failure is not null)
                {
                    TryDelete(temporary);
                    return failure;
                }

                File.Move(temporary, target, overwrite: true);
                if (string.Equals(target, job.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var part in job.Parts)
                    {
                        TryDelete(part.Path);
                    }
                }

                return new SplitAssemblyResult(SplitAssemblyOutcome.Assembled, target, null);
            }
            finally
            {
                output.Dispose();
                ActiveStaging.TryRemove(temporary, out _);
            }
        }

        return lackedSpace
            ? new SplitAssemblyResult(SplitAssemblyOutcome.NotEnoughSpace, null, $"not enough free disk space: joining the model needs {job.Size + FreeSpaceMarginBytes} bytes")
            : new SplitAssemblyResult(SplitAssemblyOutcome.NotWritable, null, "neither the AI pack folder nor the fallback folder can be written");
    }

    private async Task<SplitAssemblyResult?> CopyPartsAsync(
        SplitModelJob job,
        FileStream output,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_bufferSize];
        long written = 0;
        foreach (var part in job.Parts)
        {
            using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var input = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    whole.AppendData(buffer, 0, read);
                    partHash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    written += read;
                    progress?.Report(written);
                }

                if (copied != part.Size)
                {
                    return new SplitAssemblyResult(SplitAssemblyOutcome.PartsMissing, null, $"part {System.IO.Path.GetFileName(part.Path)} changed size while joining");
                }
            }

            if (!string.IsNullOrEmpty(part.Sha256) && !HashEquals(partHash.GetHashAndReset(), part.Sha256))
            {
                return new SplitAssemblyResult(SplitAssemblyOutcome.PartCorrupt, null, $"part {System.IO.Path.GetFileName(part.Path)} is corrupt (SHA-256 mismatch); extract it again");
            }
        }

        return HashEquals(whole.GetHashAndReset(), job.Sha256)
            ? null
            : new SplitAssemblyResult(SplitAssemblyOutcome.HashMismatch, null, "the joined model does not match its SHA-256; extract every AI pack ZIP again");
    }

    private static bool HashEquals(byte[] hash, string expectedHex) =>
        string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);

    private static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    private long? SafeFreeSpace(string folder)
    {
        try
        {
            return _freeSpace(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static long? FreeSpaceOf(string folder)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));
        return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a read-only pack keeps its parts, and a stale temporary file is
            // overwritten by the next run.
        }
    }
}
