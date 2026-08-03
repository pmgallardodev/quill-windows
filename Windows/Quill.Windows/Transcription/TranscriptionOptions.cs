using Quill.Windows.Infrastructure;

namespace Quill.Windows.Transcription;

/// <summary>
/// Ajustes de inferencia ya resueltos. Se leen una vez al construir el transcriptor
/// para que un cambio de config.json no altere una transcripcion a medias.
/// </summary>
internal sealed record TranscriptionOptions(
    string ModelPath,
    string ModelName,
    string Language,
    int Threads,
    string? VadModelPath,
    string? Prompt)
{
    public static TranscriptionOptions FromConfig() => FromConfig(ConfigStore.Load().Transcription);

    internal static TranscriptionOptions FromConfig(TranscriptionConfig config) => new(
        ConfigStore.ResolveModelPath(config.Model),
        Path.GetFileNameWithoutExtension(config.Model),
        config.Language,
        ResolveThreads(config.Threads),
        config.Vad ? ConfigStore.ResolveModelPath(config.VadModel) : null,
        NormalizePrompt(config.Prompt));

    /// <summary>
    /// Un prompt en blanco no es lo mismo que ninguno: whisper.cpp lo tokeniza igual
    /// y gasta contexto, asi que solo se conserva cuando trae texto.
    /// </summary>
    internal static string? NormalizePrompt(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();

    /// <summary>
    /// whisper.cpp trae un tope de cuatro hilos que desaprovecha los CPU actuales.
    /// La mitad de los nucleos logicos se acerca al numero de nucleos fisicos, que
    /// es donde deja de escalar, y mantiene la maquina usable durante una reunion.
    /// </summary>
    internal static int ResolveThreads(int? configured) => configured is { } value && value > 0
        ? value
        : Math.Max(1, Environment.ProcessorCount / 2);
}
