using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Sessions;

namespace Quill.Core.Speakers;

/// <summary>Colour slot and voice embedding of a speaker id (the part speakers.json does not hold).</summary>
public sealed record SpeakerVoice(string Id, int ColorSlot, float[]? Embedding);

/// <summary>Contents of speakers.resolution.json.</summary>
/// <param name="Edits">User corrections in the order they were made; replayed after each resolution.</param>
public sealed record SpeakerResolutionState(IReadOnlyList<SpeakerVoice> Voices, IReadOnlyList<SpeakerEdit> Edits)
{
    public static SpeakerResolutionState Empty { get; } = new([], []);
}

/// <summary>Persists <see cref="SpeakerResolutionState"/> per session.</summary>
public interface ISpeakerResolutionStore
{
    /// <summary><see cref="SpeakerResolutionState.Empty"/> when missing or unreadable.</summary>
    Task<SpeakerResolutionState> LoadAsync(string sessionId, CancellationToken cancellationToken);

    Task SaveAsync(string sessionId, SpeakerResolutionState state, CancellationToken cancellationToken);
}

/// <summary>
/// speakers.resolution.json in the session folder (additive v2 file; older versions ignore it):
/// <c>{ "version": 1, "voices": [{ "id", "colorSlot", "embedding"? }],
/// "edits": [{ "op": "merge", "from", "into" } | { "op": "reassign", "track", "startMs", "speakerId" }] }</c>.
/// </summary>
public sealed class FileSpeakerResolutionStore(ISessionStore sessions) : ISpeakerResolutionStore
{
    public const string FileName = "speakers.resolution.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<SpeakerResolutionState> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = await PathOfAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (path is null || !File.Exists(path))
        {
            return SpeakerResolutionState.Empty;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<StateFile>(text);
            if (file is null)
            {
                return SpeakerResolutionState.Empty;
            }

            var voices = (file.Voices ?? [])
                .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Id))
                .Select(row => new SpeakerVoice(row!.Id!, row.ColorSlot, row.Embedding))
                .ToList();
            var edits = (file.Edits ?? []).Select(ToEdit).OfType<SpeakerEdit>().ToList();
            return new SpeakerResolutionState(voices, edits);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return SpeakerResolutionState.Empty;
        }
    }

    public async Task SaveAsync(string sessionId, SpeakerResolutionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = await PathOfAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"unknown session '{sessionId}'", nameof(sessionId));
        var file = new StateFile
        {
            Version = 1,
            Voices = state.Voices.Select(voice => (VoiceRow?)new VoiceRow
            {
                Id = voice.Id,
                ColorSlot = voice.ColorSlot,
                Embedding = voice.Embedding,
            }).ToList(),
            Edits = state.Edits.Select(FromEdit).ToList(),
        };
        AtomicFile.Write(path, JsonSerializer.Serialize(file, WriteOptions));
    }

    private async Task<string?> PathOfAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session is null ? null : Path.Combine(session.Folder, FileName);
    }

    private static SpeakerEdit? ToEdit(EditRow? row) => row?.Op switch
    {
        "merge" when !string.IsNullOrWhiteSpace(row.From) && !string.IsNullOrWhiteSpace(row.Into) =>
            new MergeSpeakers(row.From, row.Into),
        "reassign" when TrackNames.TryParse(row.Track, out var track) && row.StartMs is >= 0 &&
                        SpeakerProfile.IsAssignable(row.SpeakerId) =>
            new ReassignSegment(track, TimeSpan.FromMilliseconds(row.StartMs.Value), row.SpeakerId!),
        _ => null,
    };

    private static EditRow? FromEdit(SpeakerEdit edit) => edit switch
    {
        MergeSpeakers merge => new EditRow { Op = "merge", From = merge.From, Into = merge.Into },
        ReassignSegment reassign => new EditRow
        {
            Op = "reassign",
            Track = TrackNames.ToName(reassign.Track),
            StartMs = (long)reassign.Start.TotalMilliseconds,
            SpeakerId = reassign.SpeakerId,
        },
        _ => null,
    };

    private sealed class StateFile
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("voices")]
        public List<VoiceRow?>? Voices { get; init; }

        [JsonPropertyName("edits")]
        public List<EditRow?>? Edits { get; init; }
    }

    private sealed class VoiceRow
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("colorSlot")]
        public int ColorSlot { get; init; }

        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; init; }
    }

    private sealed class EditRow
    {
        [JsonPropertyName("op")]
        public string? Op { get; init; }

        [JsonPropertyName("from")]
        public string? From { get; init; }

        [JsonPropertyName("into")]
        public string? Into { get; init; }

        [JsonPropertyName("track")]
        public string? Track { get; init; }

        [JsonPropertyName("startMs")]
        public long? StartMs { get; init; }

        [JsonPropertyName("speakerId")]
        public string? SpeakerId { get; init; }
    }
}
