using Quill.Core.Live;
using Quill.Core.Ports;
using Whisper.net;

namespace Quill.Windows.Transcription;

/// <summary>Live transcription with the AI pack's Whisper small model.</summary>
internal static class WhisperStreamingTranscriber
{
    /// <summary>Whisper small loads in a few hundred MB; inference goes through the shared scheduler.</summary>
    /// <exception cref="FileNotFoundException">The model file does not exist.</exception>
    public static IStreamingTranscriber Create(
        string modelPath,
        string language,
        int threads,
        IInferenceScheduler scheduler)
        => new ScheduledStreamingTranscriber(new WhisperChunkRecognizer(modelPath, language, threads), scheduler);

    /// <summary>
    /// Live inference shares the machine with the meeting app and the final pass, so it uses a
    /// quarter of the logical cores (at least two) instead of the final pass's half.
    /// </summary>
    internal static int LiveThreads(int processorCount) => Math.Max(2, processorCount / 4);
}

/// <summary>
/// Whisper.net recognizer for one in-memory 16 kHz mono chunk: no temporary WAV files. Each chunk
/// is decoded without the previous chunk's text as context, so one hallucination cannot carry over.
/// </summary>
internal sealed class WhisperChunkRecognizer : IChunkRecognizer
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public WhisperChunkRecognizer(string modelPath, string language, int threads)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("no se encontro el modelo Whisper de transcripcion en vivo", modelPath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var factory = WhisperFactory.FromPath(modelPath);
        try
        {
            _processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithThreads(Math.Max(1, threads))
                .WithNoContext()
                .Build();
        }
        catch
        {
            factory.Dispose();
            throw;
        }

        _factory = factory;
    }

    public async Task<IReadOnlyList<RecognizedSpan>> RecognizeAsync(
        ReadOnlyMemory<float> samples,
        CancellationToken cancellationToken)
    {
        var spans = new List<RecognizedSpan>();
        await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            spans.Add(new RecognizedSpan(segment.Start, segment.End, segment.Text ?? string.Empty));
        }

        return spans;
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync().ConfigureAwait(false);
        _factory.Dispose();
    }
}
