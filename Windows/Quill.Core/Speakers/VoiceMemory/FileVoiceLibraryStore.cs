using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Windows.Sessions;

namespace Quill.Core.Speakers.VoiceMemory;

/// <summary>Persists the <see cref="VoiceLibrary"/>.</summary>
public interface IVoiceLibraryStore
{
    /// <summary><see cref="VoiceLibrary.Empty"/> when missing or unreadable.</summary>
    Task<VoiceLibrary> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(VoiceLibrary library, CancellationToken cancellationToken);
}

/// <summary>
/// voices.json directly under the recordings root, next to the session folders (never inside
/// one, so per-meeting exports never include it). Local only:
/// <c>{ "version": 1, "autoApply": false, "voices": [{ "id", "name"?, "owner"?,
/// "samples": [{ "session", "speaker", "at", "embedding" }] }] }</c>.
/// </summary>
public sealed class FileVoiceLibraryStore(string recordingsRoot) : IVoiceLibraryStore
{
    public const string FileName = "voices.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = System.IO.Path.Combine(recordingsRoot, FileName);

    public async Task<VoiceLibrary> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return VoiceLibrary.Empty;
        }

        try
        {
            var text = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<LibraryFile>(text);
            if (file is null)
            {
                return VoiceLibrary.Empty;
            }

            var voices = (file.Voices ?? [])
                .OfType<VoiceRow>()
                .Where(row => !string.IsNullOrWhiteSpace(row.Id) && (row.Owner || !string.IsNullOrWhiteSpace(row.Name)))
                .Select(row => new VoicePrint(
                    row.Id!,
                    row.Owner ? null : row.Name!.Trim(),
                    row.Owner,
                    (row.Samples ?? [])
                        .OfType<SampleRow>()
                        .Where(sample => !string.IsNullOrWhiteSpace(sample.Session) && sample.Embedding is { Length: > 0 })
                        .Select(sample => new VoiceSample(sample.Session!, sample.Speaker ?? string.Empty, sample.Embedding!, sample.At))
                        .ToList()))
                .Where(voice => voice.Samples.Count > 0)
                .ToList();
            return new VoiceLibrary(voices, file.AutoApply);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return VoiceLibrary.Empty;
        }
    }

    public Task SaveAsync(VoiceLibrary library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        cancellationToken.ThrowIfCancellationRequested();
        var file = new LibraryFile
        {
            Version = 1,
            AutoApply = library.AutoApply,
            Voices = library.Voices.Select(voice => (VoiceRow?)new VoiceRow
            {
                Id = voice.Id,
                Name = voice.Name,
                Owner = voice.IsOwner,
                Samples = voice.Samples.Select(sample => (SampleRow?)new SampleRow
                {
                    Session = sample.SessionId,
                    Speaker = sample.SpeakerId,
                    At = sample.At,
                    Embedding = sample.Embedding,
                }).ToList(),
            }).ToList(),
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.Write(Path, JsonSerializer.Serialize(file, WriteOptions));
        return Task.CompletedTask;
    }

    private sealed class LibraryFile
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("autoApply")]
        public bool AutoApply { get; init; }

        [JsonPropertyName("voices")]
        public List<VoiceRow?>? Voices { get; init; }
    }

    private sealed class VoiceRow
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("owner")]
        public bool Owner { get; init; }

        [JsonPropertyName("samples")]
        public List<SampleRow?>? Samples { get; init; }
    }

    private sealed class SampleRow
    {
        [JsonPropertyName("session")]
        public string? Session { get; init; }

        [JsonPropertyName("speaker")]
        public string? Speaker { get; init; }

        [JsonPropertyName("at")]
        public DateTimeOffset At { get; init; }

        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; init; }
    }
}
