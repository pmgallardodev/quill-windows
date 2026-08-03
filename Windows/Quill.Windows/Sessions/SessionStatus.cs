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

            var status = JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(status?.State) ? null : status;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string sessionDirectory) => AtomicFile.Write(
        PathFor(sessionDirectory),
        JsonSerializer.Serialize(this, WriteOptions));
}
