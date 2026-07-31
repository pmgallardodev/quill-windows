using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Windows.Infrastructure;

internal static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "quill",
        "config.json");

    public static string DefaultRecordingsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Recordings");

    public static QuillConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new QuillConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<QuillConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                ?? new QuillConfig();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"configuracion invalida: {ConfigPath}", exception);
        }
    }

    public static string ResolveRecordingsRoot(string? commandLineOverride)
    {
        var configured = Load().RecordingsDirectory;
        var path = commandLineOverride ?? configured ?? DefaultRecordingsRoot;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public static string ModelPath => Path.Combine(
        AppContext.BaseDirectory,
        "models",
        "ggml-large-v3-q5_0.bin");
}

internal sealed class QuillConfig
{
    [JsonPropertyName("recordings_dir")]
    public string? RecordingsDirectory { get; init; }

    [JsonPropertyName("transcription")]
    public TranscriptionConfig Transcription { get; init; } = new();
}

internal sealed class TranscriptionConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("engine")]
    public string Engine { get; init; } = "whisper";

    [JsonPropertyName("language")]
    public string Language { get; init; } = "es";
}
