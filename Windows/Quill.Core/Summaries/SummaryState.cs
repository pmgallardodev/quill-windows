using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Quill.Windows.Sessions;

namespace Quill.Core.Summaries;

/// <summary>
/// State of a session's final summary that is not part of the summary itself. Lives in a sidecar
/// (summary.state.json) so summary.json keeps its closed schema and older readers are unaffected.
/// </summary>
public sealed record SummaryState
{
    public static readonly SummaryState Current = new();

    /// <summary>
    /// The transcript changed after the summary was generated (e.g. a transcription retry), so the
    /// summary may no longer match it. The UI offers "Regenerar"; regenerating clears the flag.
    /// </summary>
    [JsonPropertyName("outdated")]
    public bool Outdated { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("since")]
    public DateTimeOffset? Since { get; init; }
}

/// <summary>Summary sidecars of a session: the outdated flag and a one-generation backup.</summary>
public interface ISummaryStateStore
{
    /// <summary>Whether a readable final summary (summary.json) exists.</summary>
    Task<bool> HasFinalSummaryAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary><see cref="SummaryState.Current"/> when there is no sidecar or it is unreadable.</summary>
    Task<SummaryState> LoadStateAsync(string sessionId, CancellationToken cancellationToken);

    Task MarkOutdatedAsync(string sessionId, string reason, CancellationToken cancellationToken);

    Task ClearOutdatedAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Keeps <paramref name="previous"/> as summary.prev.json, replacing any older backup.</summary>
    Task BackupAsync(string sessionId, MeetingSummary previous, CancellationToken cancellationToken);
}

/// <summary><see cref="ISummaryStateStore"/> next to summary.json in the session folder.</summary>
public sealed class FileSummaryStateStore : ISummaryStateStore
{
    public const string StateFile = "summary.state.json";
    public const string PreviousFile = "summary.prev.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private readonly FileSessionStore _sessions;
    private readonly IClock _clock;

    public FileSummaryStateStore(FileSessionStore sessions, IClock? clock = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? SystemClock.Instance;
    }

    public async Task<bool> HasFinalSummaryAsync(string sessionId, CancellationToken cancellationToken) =>
        await _sessions.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<SummaryState> LoadStateAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = PathOf(sessionId, StateFile);
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SummaryState>(text, Options) ?? SummaryState.Current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing (FileNotFound/DirectoryNotFound are IOExceptions) or corrupt: not outdated.
            return SummaryState.Current;
        }
    }

    public Task MarkOutdatedAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = new SummaryState { Outdated = true, Reason = reason, Since = _clock.Now };
        AtomicFile.Write(PathOf(sessionId, StateFile), JsonSerializer.Serialize(state, Options));
        return Task.CompletedTask;
    }

    public Task ClearOutdatedAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathOf(sessionId, StateFile));
        return Task.CompletedTask;
    }

    public Task BackupAsync(string sessionId, MeetingSummary previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.Write(PathOf(sessionId, PreviousFile), MeetingSummaryJson.Serialize(previous));
        return Task.CompletedTask;
    }

    private string PathOf(string sessionId, string file) => Path.Combine(_sessions.FolderOf(sessionId), file);
}
