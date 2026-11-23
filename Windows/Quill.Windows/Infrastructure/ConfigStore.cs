using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.AiPack;
using Quill.Core.Ports;
using Quill.Core.Settings;

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
            var config = JsonSerializer.Deserialize<QuillConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                ?? new QuillConfig();
            if (config.Recording is null || config.Transcription is null)
            {
                throw new InvalidOperationException($"configuracion invalida: {ConfigPath}");
            }

            return config;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"configuracion invalida: {ConfigPath}", exception);
        }
    }

    public static string ResolveRecordingsRoot(string? commandLineOverride)
    {
        var configured = Load().RecordingsDirectory;
        var path = string.IsNullOrWhiteSpace(commandLineOverride)
            ? configured
            : commandLineOverride;
        path = string.IsNullOrWhiteSpace(path) ? DefaultRecordingsRoot : path;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    /// <summary>
    /// Un nombre suelto se busca en models\ junto al ejecutable; una ruta absoluta se
    /// respeta tal cual, de modo que se puede probar un modelo sin copiarlo al paquete.
    /// </summary>
    public static string ResolveModelPath(string fileName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(fileName);
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        if (string.IsNullOrWhiteSpace(expanded) ||
            expanded.Contains(Path.DirectorySeparatorChar) ||
            expanded.Contains(Path.AltDirectorySeparatorChar) ||
            expanded.Contains('\\') || expanded.Contains('/') ||
            expanded is "." or "..")
        {
            throw new InvalidOperationException("el modelo debe ser un nombre de archivo o una ruta absoluta");
        }

        return Path.Combine(ModelsDirectory, expanded);
    }

    public static string ModelPath => ResolveModelPath(Load().Transcription.Model);

    /// <summary>%APPDATA% (roaming): parent of the default user templates folder "Quill\templates".</summary>
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Quill Next settings ("ai", "summaries", "templates", "export" sections of config.json).
    /// Lenient: a missing file, a broken file or a bad value yields the defaults, so the app
    /// always starts. The v1 keys keep being read by <see cref="Load"/>.
    /// </summary>
    public static QuillSettings LoadSettings()
    {
        try
        {
            return SettingsDocument.Parse(File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new QuillSettings();
        }
    }

    /// <summary>
    /// Writes the settings sections into config.json, keeping every other key. Refuses
    /// (<see cref="FormatException"/>) to replace a config.json that is not valid JSON.
    /// </summary>
    public static void SaveSettings(QuillSettings settings)
    {
        var existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        var merged = SettingsDocument.Merge(existing, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, merged);
        File.Move(temporary, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// Saves "ai.pack_path", keeping every other setting. Used after the in-app AI-pack download
    /// installs into %LOCALAPPDATA%\Quill\ai-pack because the app folder is read-only.
    /// </summary>
    public static QuillSettings SaveAiPackPath(string packRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        var current = LoadSettings();
        var updated = current with { Ai = current.Ai with { PackPath = packRoot } };
        SaveSettings(updated);
        return updated;
    }

    /// <summary>The "api" section of config.json (local API). Lenient like <see cref="LoadSettings"/>: off on any problem.</summary>
    public static ApiSettings LoadApiSettings()
    {
        try
        {
            return ApiSettingsDocument.Parse(File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ApiSettings();
        }
    }

    /// <summary>Writes the "api" section, keeping every other key. Same atomic replace as <see cref="SaveSettings"/>.</summary>
    public static void SaveApiSettings(ApiSettings settings)
    {
        var existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        var merged = ApiSettingsDocument.Merge(existing, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, merged);
        File.Move(temporary, ConfigPath, overwrite: true);
    }

    public static string TemplatesFolder(QuillSettings settings) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.Templates.ResolveFolder(RoamingAppData)));

    /// <summary>AI pack locator honoring the "ai.pack_path" override (empty: "ai-pack" next to the app).</summary>
    public static IAiPackLocator CreateAiPackLocator(QuillSettings settings) =>
        new ManifestAiPackLocator(
            AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(settings.Ai.PackPath) ? null : Environment.ExpandEnvironmentVariables(settings.Ai.PackPath));
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

    public const string OffDetection = "off";
    public const string AskDetection = "ask";
    public const string AutoDetection = "auto";

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

    /// <summary>
    /// Que hacer cuando una aplicacion de reuniones abre el microfono: "off" no
    /// mira nada, "ask" avisa con un globo y "auto" empieza a grabar solo. Por
    /// defecto "ask": arrancar una grabacion sin permiso seria pasarse.
    /// </summary>
    [JsonPropertyName("meeting_detection")]
    public string MeetingDetection { get; init; } = AskDetection;

    /// <summary>Ctrl+Shift+R para grabar y Ctrl+Shift+M para marcar, sin foco.</summary>
    [JsonPropertyName("global_hotkeys")]
    public bool GlobalHotkeys { get; init; } = true;

    public bool DetectsMeetings => !string.Equals(MeetingDetection, OffDetection, StringComparison.OrdinalIgnoreCase);

    public bool StartsMeetingsAutomatically =>
        string.Equals(MeetingDetection, AutoDetection, StringComparison.OrdinalIgnoreCase);

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
