using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Windows.Sessions;

internal sealed class SessionMetadata
{
    [JsonPropertyName("started")]
    public DateTimeOffset Started { get; init; }

    [JsonPropertyName("ended")]
    public DateTimeOffset Ended { get; init; }

    [JsonPropertyName("duration_seconds")]
    public long DurationSeconds { get; init; }

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; init; } = new();

    [JsonPropertyName("start_offset_ms")]
    public Dictionary<string, long> StartOffsetMs { get; init; } = new();

    public static SessionMetadata Read(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "meta.json");
        return JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"no se pudo leer {path}");
    }
}

internal sealed class TranscriptDocument
{
    [JsonPropertyName("engine")]
    public required string Engine { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("segments")]
    public required IReadOnlyList<TranscriptSegment> Segments { get; init; }

    public void Write(string sessionDirectory)
    {
        AtomicFile.Write(
            Path.Combine(sessionDirectory, "transcript.json"),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        AtomicFile.Write(Path.Combine(sessionDirectory, "transcript.md"), Render(sessionDirectory));
    }

    private string Render(string sessionDirectory)
    {
        var output = new StringBuilder();
        output.AppendLine($"# {Path.GetFileName(sessionDirectory)}");
        output.AppendLine();
        output.AppendLine($"engine: {Engine} ({Model})");
        output.AppendLine();

        foreach (var segment in Segments)
        {
            output.AppendLine($"**[{Clock(segment.StartMs)}] {segment.Speaker}:** {segment.Text.Trim()}");
            output.AppendLine();
        }

        return output.ToString();
    }

    private static string Clock(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }
}

internal sealed record TranscriptSegment(
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("start_ms")] long StartMs,
    [property: JsonPropertyName("end_ms")] long EndMs,
    [property: JsonPropertyName("text")] string Text);

internal static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
