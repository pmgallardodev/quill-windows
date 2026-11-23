using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Windows.Sessions;

/// <summary>
/// Estado durable de una sesion. Enriquece lo que el disco ya dice con lo que no
/// puede saber: el porque de un fallo, el modelo usado y cuanto tardo. No es la
/// fuente de verdad: si falta o esta corrupto, el estado se deriva de los archivos.
/// </summary>
internal sealed class SessionStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Disabled = "disabled";
    public const string FailureMarkerFile = ".quill-failed";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("elapsed_seconds")]
    public double? ElapsedSeconds { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static string PathFor(string sessionDirectory) =>
        Path.Combine(sessionDirectory, "status.json");

    public static string FailureMarkerPathFor(string sessionDirectory) =>
        Path.Combine(sessionDirectory, FailureMarkerFile);

    public static bool HasFailureMarker(string sessionDirectory)
    {
        try
        {
            var path = FailureMarkerPathFor(sessionDirectory);
            return File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? ReadFailureMarker(string sessionDirectory)
    {
        try
        {
            return HasFailureMarker(sessionDirectory)
                ? File.ReadAllText(FailureMarkerPathFor(sessionDirectory)).Trim()
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static void WriteFailureMarker(string sessionDirectory, string error) =>
        AtomicFile.Write(FailureMarkerPathFor(sessionDirectory), error);

    public static void ClearFailureMarker(string sessionDirectory)
    {
        try
        {
            File.Delete(FailureMarkerPathFor(sessionDirectory));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A missing or locked marker must not hide the original retry result.
        }
    }

    /// <summary>Devuelve null ante cualquier problema. Leer el estado nunca debe lanzar.</summary>
    public static SessionStatus? TryRead(string sessionDirectory)
    {
        var path = PathFor(sessionDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var status = JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(status?.State))
            {
                return null;
            }

            if (status.ElapsedSeconds is { } elapsed &&
                (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > TimeSpan.MaxValue.TotalSeconds))
            {
                return null;
            }

            return status;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    public void Write(string sessionDirectory) => AtomicFile.Write(
        PathFor(sessionDirectory),
        JsonSerializer.Serialize(this, WriteOptions));
}
