using Quill.Core.Ports;

namespace Quill.Core.Tests.Live;

/// <summary>Synthetic 16 kHz signals for chunker tests.</summary>
internal static class SignalFactory
{
    public static float[] Silence(double seconds) => new float[Samples(seconds)];

    /// <summary>A 220 Hz tone; RMS is amplitude / sqrt(2).</summary>
    public static float[] Tone(double seconds, float amplitude = 0.4f)
    {
        var samples = new float[Samples(seconds)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = amplitude * MathF.Sin(2 * MathF.PI * 220 * index / AudioChunk.SampleRate);
        }

        return samples;
    }

    /// <summary>
    /// Speech-like signal: a tone with an 80 ms dip every 400 ms, like the gaps between
    /// syllables that let the chunker's noise floor tell speech from steady noise.
    /// </summary>
    public static float[] Speech(double seconds, float amplitude = 0.4f)
    {
        var samples = Tone(seconds, amplitude);
        var period = Samples(0.4);
        var gap = Samples(0.08);
        for (var index = 0; index < samples.Length; index++)
        {
            if (index % period >= period - gap)
            {
                samples[index] = 0f;
            }
        }

        return samples;
    }

    /// <summary>Deterministic white-ish noise with roughly the given RMS.</summary>
    public static float[] Noise(double seconds, float rms)
    {
        var random = new Random(1234);
        var samples = new float[Samples(seconds)];
        var amplitude = rms * MathF.Sqrt(3);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = amplitude * (float)(random.NextDouble() * 2 - 1);
        }

        return samples;
    }

    public static float[] Concat(params float[][] parts) => parts.SelectMany(part => part).ToArray();

    public static int Samples(double seconds) => (int)Math.Round(seconds * AudioChunk.SampleRate);
}
