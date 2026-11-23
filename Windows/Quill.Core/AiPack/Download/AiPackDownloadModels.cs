using Quill.Core.Ports;

namespace Quill.Core.AiPack.Download;

public enum AiPackDownloadPhase
{
    Downloading,
    Verifying,
    Completed,
}

/// <summary>Progress of one install run. "Total" values cover the whole plan, including bytes
/// already on disk when the run started.</summary>
public sealed record AiPackDownloadProgress(
    AiPackComponent Component,
    AiPackDownloadPhase Phase,
    long ComponentBytes,
    long ComponentTotal,
    long TotalDone,
    long TotalBytes,
    double BytesPerSecond);

public enum AiPackDownloadFailure
{
    /// <summary>A URL or redirect pointed outside <see cref="AiPackHostPolicy"/>.</summary>
    HostNotAllowed,

    /// <summary>A URL or redirect was not HTTPS.</summary>
    InsecureUrl,

    TooManyRedirects,

    /// <summary>The server answered an unexpected status (see <see cref="AiPackDownloadException.StatusCode"/>).</summary>
    HttpStatus,

    /// <summary>The body was (or announced to be) larger than the pinned size, or the server's
    /// range did not match; the partial file is deleted.</summary>
    SizeMismatch,

    /// <summary>The connection ended before the pinned size; the partial file is kept for resume.</summary>
    Incomplete,

    /// <summary>The finished file does not hash to the pinned SHA-256; it is deleted.</summary>
    HashMismatch,

    /// <summary>No data arrived within the stall timeout; the partial file is kept for resume.</summary>
    Stalled,
}

public sealed class AiPackDownloadException(
    AiPackDownloadFailure failure,
    AiPackComponent? component,
    string message,
    int? statusCode = null,
    Exception? inner = null) : Exception(message, inner)
{
    public AiPackDownloadFailure Failure { get; } = failure;

    public AiPackComponent? Component { get; } = component;

    public int? StatusCode { get; } = statusCode;
}
