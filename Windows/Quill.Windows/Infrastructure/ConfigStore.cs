using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Windows.Infrastructure;

internal static class ConfigStore
{
    /// <summary>
    /// Modelo incluido en el paquete portable. large-v3-turbo poda el decoder de 32
    /// capas a 4: mucho mas rapido que large-v3 con una precision equivalente en
    /// espanol, y la mitad de tamano en disco.
    /// </summary>
    public const string DefaultModelFile = "ggml-large-v3-turbo-q5_0.bin";

    /// <summary>Modelo de deteccion de voz, usado solo cuando "vad" esta activo.</summary>
    public const string DefaultVadModelFile = "ggml-silero-v5.1.2.bin";

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

    public static string ModelsDirectory => Path.Combine(AppContext.BaseDirectory, "models");

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

    /// <summary>
    /// Un nombre suelto se busca en models\ junto al ejecutable; una ruta absoluta se
    /// respeta tal cual, de modo que se puede probar un modelo sin copiarlo al paquete.
    /// </summary>
    public static string ResolveModelPath(string fileName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(fileName);
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.Combine(ModelsDirectory, expanded);
    }

    public static string ModelPath => ResolveModelPath(Load().Transcription.Model);
}

internal sealed class QuillConfig
{
    [JsonPropertyName("recordings_dir")]
    public string? RecordingsDirectory { get; init; }

    [JsonPropertyName("recording")]
    public RecordingConfig Recording { get; init; } = new();

    [JsonPropertyName("transcription")]
    public TranscriptionConfig Transcription { get; init; } = new();
}

internal sealed class RecordingConfig
{
    public const string VoiceQuality = "voice";
    public const string NativeQuality = "native";

    /// <summary>
    /// "voice" graba a 16 kHz mono 16 bits, que es exactamente lo que consume
    /// Whisper y ocupa unas once veces menos que el formato del dispositivo.
    /// "native" conserva el formato original de WASAPI.
    /// </summary>
    [JsonPropertyName("quality")]
    public string Quality { get; init; } = VoiceQuality;

    /// <summary>Borra mic.wav y system.wav cuando el transcript queda escrito.</summary>
    [JsonPropertyName("delete_audio_after_transcription")]
    public bool DeleteAudioAfterTranscription { get; init; }

    public bool RecordsVoiceQuality =>
        !string.Equals(Quality, NativeQuality, StringComparison.OrdinalIgnoreCase);
}

internal sealed class TranscriptionConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("engine")]
    public string Engine { get; init; } = "whisper";

    [JsonPropertyName("language")]
    public string Language { get; init; } = "es";

    /// <summary>Nombre dentro de models\ o ruta absoluta a un .bin de whisper.cpp.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = ConfigStore.DefaultModelFile;

    /// <summary>Hilos de inferencia. Sin valor usa la mitad de los nucleos logicos.</summary>
    [JsonPropertyName("threads")]
    public int? Threads { get; init; }

    /// <summary>
    /// Salta los silencios antes de transcribir. Desactivado por defecto: cambia como
    /// se trocea el audio, asi que conviene comparar el resultado antes de fiarse.
    /// </summary>
    [JsonPropertyName("vad")]
    public bool Vad { get; init; }

    [JsonPropertyName("vad_model")]
    public string VadModel { get; init; } = ConfigStore.DefaultVadModelFile;

    /// <summary>
    /// Vocabulario propio que se pasa a Whisper como contexto inicial: nombres de
    /// empresa, clientes y jerga tecnica que de otro modo transcribe fonéticamente.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }
}
