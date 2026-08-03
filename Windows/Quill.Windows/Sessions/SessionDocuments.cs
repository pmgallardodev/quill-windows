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

    /// <summary>Nombre que el usuario da a la sesion. El de la carpeta si esta vacio.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Marcas puestas durante la grabacion, en ms desde el inicio.</summary>
    [JsonPropertyName("markers")]
    public List<long> Markers { get; init; } = new();

    public static SessionMetadata Read(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "meta.json");
        var metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"no se pudo leer {path}");
        if (metadata.Files is null || metadata.StartOffsetMs is null || metadata.Markers is null)
        {
            throw new InvalidDataException($"metadatos incompletos: {path}");
        }

        return metadata;
    }

    public static SessionMetadata? TryRead(string sessionDirectory)
    {
        try
        {
            return File.Exists(Path.Combine(sessionDirectory, "meta.json"))
                ? Read(sessionDirectory)
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string sessionDirectory) => AtomicFile.Write(
        Path.Combine(sessionDirectory, "meta.json"),
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Renombrar no puede perder los offsets ni las marcas, asi que se relee el
    /// archivo entero y solo se sustituye el titulo.
    /// </summary>
    public static bool TryUpdateTitle(string sessionDirectory, string? title)
    {
        var metadata = TryRead(sessionDirectory);
        if (metadata is null)
        {
            return false;
        }

        try
        {
            new SessionMetadata
            {
                Started = metadata.Started,
                Ended = metadata.Ended,
                DurationSeconds = metadata.DurationSeconds,
                Files = metadata.Files,
                StartOffsetMs = metadata.StartOffsetMs,
                Markers = metadata.Markers,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            }.Write(sessionDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        // transcript.json is the completion marker used by recovery. Write the
        // human-readable copy first so a crash or failure cannot expose a JSON-only
        // transcript as complete.
        AtomicFile.Write(Path.Combine(sessionDirectory, "transcript.md"), Render(sessionDirectory));
        AtomicFile.Write(
            Path.Combine(sessionDirectory, "transcript.json"),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                // The original write error is the useful one to report.
            }

            throw;
        }
    }
}
