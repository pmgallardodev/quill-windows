using System.Diagnostics;
using SherpaOnnx;
using Whisper.net;

namespace Quill.Bench;

/// <summary>Whisper.net live-chunk latency and sherpa-onnx diarization smoke checks.</summary>
public static class AudioChecks
{
    /// <summary>
    /// Reads a PCM16 or IEEE float32 WAV (any rate, any channel count, e.g. a Quill session track),
    /// downmixes to mono and linearly resamples to 16 kHz.
    /// </summary>
    public static float[] ReadWav16kMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int channels = 0, rate = 0, bits = 0, format = 0;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(bytes, offset + 8);
                channels = BitConverter.ToInt16(bytes, offset + 10);
                rate = BitConverter.ToInt32(bytes, offset + 12);
                bits = BitConverter.ToInt16(bytes, offset + 22);
                if (format == 0xFFFE && size >= 40)
                {
                    format = BitConverter.ToUInt16(bytes, offset + 8 + 24); // WAVE_FORMAT_EXTENSIBLE subformat
                }
            }
            else if (id == "data")
            {
                var isFloat = format == 3 && bits == 32;
                if (!isFloat && !(format == 1 && bits == 16))
                {
                    throw new InvalidDataException($"{path}: unsupported WAV format {format}/{bits} bit");
                }

                var bytesPerSample = bits / 8;
                var frames = Math.Min(size, bytes.Length - offset - 8) / (bytesPerSample * channels);
                var mono = new float[frames];
                for (var f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++)
                    {
                        var p = offset + 8 + (f * channels + c) * bytesPerSample;
                        sum += isFloat ? BitConverter.ToSingle(bytes, p) : BitConverter.ToInt16(bytes, p) / 32768f;
                    }

                    mono[f] = sum / channels;
                }

                return rate == 16000 ? mono : Resample(mono, rate, 16000);
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path}: no data chunk");
    }

    private static float[] Resample(float[] input, int from, int to)
    {
        var output = new float[(long)input.Length * to / from];
        var step = (double)from / to;
        for (var i = 0; i < output.Length; i++)
        {
            var pos = i * step;
            var i0 = (int)pos;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = (float)(pos - i0);
            output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }

        return output;
    }

    public static async Task WhisperLatencyAsync(string modelPath, string wavPath, string? vadModelPath,
        int[] chunkSeconds, int repeats, int threads, bool useGpu)
    {
        var audio = ReadWav16kMono(wavPath);
        Console.WriteLine($"audio: {audio.Length / 16000.0:F1}s, model: {Path.GetFileName(modelPath)}, " +
                          $"threads={threads}, gpu={useGpu}");
        var load = Stopwatch.StartNew();
        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        await using var processor = factory.CreateBuilder().WithLanguage("es").WithThreads(threads).WithNoContext().Build();
        Console.WriteLine($"load: {load.Elapsed.TotalSeconds:F2}s");

        // Warm-up so the first measured call does not include lazy initialization.
        await foreach (var _ in processor.ProcessAsync(audio.AsMemory(0, 16000 * 2))) { }

        foreach (var seconds in chunkSeconds)
        {
            var length = seconds * 16000;
            if (length > audio.Length)
            {
                Console.WriteLine($"chunk {seconds}s: audio too short");
                continue;
            }

            var timings = new List<double>();
            string text = "";
            for (var i = 0; i < repeats; i++)
            {
                var start = (i * length) % Math.Max(1, audio.Length - length);
                var sw = Stopwatch.StartNew();
                var sb = new System.Text.StringBuilder();
                await foreach (var seg in processor.ProcessAsync(audio.AsMemory(start, length)))
                {
                    sb.Append(seg.Text);
                }

                timings.Add(sw.Elapsed.TotalMilliseconds);
                if (i == 0) text = sb.ToString().Trim();
            }

            timings.Sort();
            var median = timings[timings.Count / 2];
            Console.WriteLine($"chunk {seconds}s: median {median:F0} ms, min {timings[0]:F0}, max {timings[^1]:F0}, " +
                              $"RTF {median / (seconds * 1000):F2} | \"{Truncate(text, 90)}\"");
        }

        if (vadModelPath is not null && File.Exists(vadModelPath))
        {
            using var vadFactory = WhisperVadFactory.FromPath(vadModelPath);
            using var vad = vadFactory.CreateBuilder().WithThreshold(0.5f)
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(300)).Build();
            var sw = Stopwatch.StartNew();
            var segments = vad.DetectSpeech(audio).ToList();
            Console.WriteLine($"VAD (silero {Path.GetFileName(vadModelPath)}): {segments.Count} speech segments over " +
                              $"{audio.Length / 16000.0:F0}s audio in {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
    }

    public static void Diarize(string wavPath, string segmentationModel, string embeddingModel, int expectedSpeakers, int threads, int loop = 1)
    {
        // --loop repeats the clip to measure throughput on longer audio (speaker count stays the same).
        var once = ReadWav16kMono(wavPath);
        var audio = Enumerable.Repeat(once, Math.Max(1, loop)).SelectMany(a => a).ToArray();
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentationModel;
        config.Embedding.Model = embeddingModel;
        config.Clustering.NumClusters = expectedSpeakers;
        config.Clustering.Threshold = 0.5f;
        config.Segmentation.NumThreads = threads;
        config.Embedding.NumThreads = threads;
        var sw = Stopwatch.StartNew();
        using var diarization = new OfflineSpeakerDiarization(config);
        var created = sw.Elapsed.TotalMilliseconds;
        var segments = diarization.Process(audio);
        Console.WriteLine($"sherpa-onnx diarization: sampleRate={diarization.SampleRate}, init {created:F0} ms, " +
                          $"process {sw.Elapsed.TotalMilliseconds - created:F0} ms for {audio.Length / 16000.0:F0}s audio");
        foreach (var s in segments)
        {
            Console.WriteLine($"  {s.Start,7:F2}-{s.End,7:F2}  speaker_{s.Speaker}");
        }

        var extractorConfig = new SpeakerEmbeddingExtractorConfig { Model = embeddingModel };
        using var extractor = new SpeakerEmbeddingExtractor(extractorConfig);
        var stream = extractor.CreateStream();
        stream.AcceptWaveform(16000, audio[..Math.Min(audio.Length, 16000 * 3)]);
        stream.InputFinished();
        var embedding = extractor.Compute(stream);
        Console.WriteLine($"speaker embedding: dim {extractor.Dim}, first values [{string.Join(", ", embedding.Take(3).Select(v => v.ToString("F3")))}]");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
