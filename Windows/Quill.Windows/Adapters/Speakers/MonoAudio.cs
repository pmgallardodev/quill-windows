using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Windows.Audio;

namespace Quill.Windows.Adapters.Speakers;

/// <summary>Reads any audio file NAudio understands as mono float samples.</summary>
internal static class MonoAudio
{
    /// <summary>Whole file, mono, resampled to <paramref name="sampleRate"/>.</summary>
    public static float[] Read(string path, int sampleRate, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels > 1)
        {
            source = new MonoDownmixSampleProvider(source);
        }

        if (source.WaveFormat.SampleRate != sampleRate)
        {
            source = new WdlResamplingSampleProvider(source, sampleRate);
        }

        var samples = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * sampleRate + sampleRate));
        var buffer = new float[sampleRate];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return samples.ToArray();
    }
}
