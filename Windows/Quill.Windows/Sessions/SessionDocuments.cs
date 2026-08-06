using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"metadatos enlazados no permitidos: {path}");
        }

        var metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"no se pudo leer {path}");
        if (metadata.Files is null || metadata.StartOffsetMs is null || metadata.Markers is null)
        {
            throw new InvalidDataException($"metadatos incompletos: {path}");
        }

        if (metadata.Started == default || metadata.Ended < metadata.Started ||
            metadata.DurationSeconds < 0 ||
            metadata.DurationSeconds > (long)TimeSpan.MaxValue.TotalSeconds ||
            metadata.Files.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            metadata.StartOffsetMs.Any(pair =>
                pair.Value < 0 || pair.Value > (long)TimeSpan.MaxValue.TotalMilliseconds) ||
            metadata.Markers.Any(marker => marker < 0))
        {
            throw new InvalidDataException($"metadatos invalidos: {path}");
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
            var path = Path.Combine(sessionDirectory, "meta.json");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (document is null)
            {
                return false;
            }

            var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            var existingTitle = document.Select(pair => pair.Key).FirstOrDefault(key =>
                string.Equals(key, "title", StringComparison.OrdinalIgnoreCase));
            if (existingTitle is not null)
            {
                document[existingTitle] = normalizedTitle;
            }
            else if (normalizedTitle is not null)
            {
                document["title"] = normalizedTitle;
            }

            AtomicFile.Write(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
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

    public static bool TryRead(string sessionDirectory, out TranscriptDocument? document)
    {
        document = null;
        var path = Path.Combine(sessionDirectory, "transcript.json");
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var candidate = JsonSerializer.Deserialize<TranscriptDocument>(File.ReadAllText(path));
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Engine) ||
                string.IsNullOrWhiteSpace(candidate.Model) || candidate.Segments is null ||
                candidate.Segments.Any(segment => segment is null ||
                    string.IsNullOrWhiteSpace(segment.Speaker) || segment.Text is null ||
                    segment.StartMs < 0 || segment.EndMs < segment.StartMs ||
                    segment.EndMs > (long)TimeSpan.MaxValue.TotalMilliseconds))
            {
                return false;
            }

            document = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public void Write(string sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(Engine) || string.IsNullOrWhiteSpace(Model) || Segments is null ||
            Segments.Any(segment => segment is null || string.IsNullOrWhiteSpace(segment.Speaker) ||
                string.IsNullOrWhiteSpace(segment.Text) || segment.StartMs < 0 ||
                segment.EndMs < segment.StartMs ||
                segment.EndMs > (long)TimeSpan.MaxValue.TotalMilliseconds))
        {
            throw new InvalidDataException("transcript invalido");
        }

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
        var span = TimeSpan.FromMilliseconds(
            Math.Clamp(milliseconds, 0L, (long)TimeSpan.MaxValue.TotalMilliseconds));
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
