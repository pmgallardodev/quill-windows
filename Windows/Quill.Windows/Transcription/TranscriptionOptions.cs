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
    string? VadModelPath)
{
    public static TranscriptionOptions FromConfig()
    {
        var config = ConfigStore.Load().Transcription;
        return new TranscriptionOptions(
            ConfigStore.ResolveModelPath(config.Model),
            Path.GetFileNameWithoutExtension(config.Model),
            config.Language,
            ResolveThreads(config.Threads),
            config.Vad ? ConfigStore.ResolveModelPath(config.VadModel) : null);
    }

    /// <summary>
    /// whisper.cpp trae un tope de cuatro hilos que desaprovecha los CPU actuales.
    /// La mitad de los nucleos logicos se acerca al numero de nucleos fisicos, que
    /// es donde deja de escalar, y mantiene la maquina usable durante una reunion.
    /// </summary>
    internal static int ResolveThreads(int? configured) => configured is { } value && value > 0
        ? value
        : Math.Max(1, Environment.ProcessorCount / 2);
}
