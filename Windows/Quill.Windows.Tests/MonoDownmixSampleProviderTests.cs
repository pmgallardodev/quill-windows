using NAudio.Wave;
using Quill.Windows.Audio;
using Xunit;

namespace Quill.Windows.Tests;

public class MonoDownmixSampleProviderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void ReportsMonoAtTheSourceSampleRate(int channels)
    {
        var downmix = new MonoDownmixSampleProvider(new ArraySampleProvider(44_100, channels));

        Assert.Equal(1, downmix.WaveFormat.Channels);
        Assert.Equal(44_100, downmix.WaveFormat.SampleRate);
    }

    [Fact]
    public void PassesMonoThrough()
    {
        var downmix = new MonoDownmixSampleProvider(new ArraySampleProvider(16_000, 1, 0.25f, -0.5f, 1f));

        Assert.Equal(new[] { 0.25f, -0.5f, 1f }, ReadAll(downmix));
    }

    [Fact]
    public void AveragesStereoFrames()
    {
        var downmix = new MonoDownmixSampleProvider(
            new ArraySampleProvider(16_000, 2, 1f, 0f, -0.5f, 0.5f, 0.2f, 0.2f));

        Assert.Equal(new[] { 0.5f, 0f, 0.2f }, ReadAll(downmix));
    }

    [Fact]
    public void AveragesSixChannelFrames()
    {
        var downmix = new MonoDownmixSampleProvider(new ArraySampleProvider(
            48_000,
            6,
            1f, 2f, 3f, 4f, 5f, 6f,
            0f, 0f, 0f, 0f, 0f, 6f));

        Assert.Equal(new[] { 3.5f, 1f }, ReadAll(downmix));
    }

    [Fact]
    public void EmitsOneSamplePerFrame_SoTheOutputIsSixTimesShorter()
    {
        var frames = 100;
        var samples = new float[frames * 6];
        Array.Fill(samples, 0.5f);

        var read = ReadAll(new MonoDownmixSampleProvider(new ArraySampleProvider(48_000, 6, samples)));

        Assert.Equal(frames, read.Length);
        Assert.All(read, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void KeepsAveragingAcrossReadsSmallerThanTheSource()
    {
        var downmix = new MonoDownmixSampleProvider(new ArraySampleProvider(
            16_000,
            4,
            1f, 1f, 1f, 1f,
            2f, 2f, 2f, 2f,
            3f, 3f, 3f, 3f));

        var buffer = new float[1];
        var first = downmix.Read(buffer, 0, 1);
        var firstSample = buffer[0];
        downmix.Read(buffer, 0, 1);

        Assert.Equal(1, first);
        Assert.Equal(1f, firstSample);
        Assert.Equal(2f, buffer[0]);
    }

    [Fact]
    public void WritesAtTheRequestedOffset()
    {
        var downmix = new MonoDownmixSampleProvider(new ArraySampleProvider(16_000, 2, 1f, 1f));
        var buffer = new float[3];

        var read = downmix.Read(buffer, 2, 1);

        Assert.Equal(1, read);
        Assert.Equal(new[] { 0f, 0f, 1f }, buffer);
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var output = new List<float>();
        var buffer = new float[64];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.AddRange(buffer.Take(read));
        }

        return output.ToArray();
    }

    /// <summary>Origen de muestras intercaladas que no impone ningun tamano de lectura.</summary>
    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(int sampleRate, int channels, params float[] samples)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _samples = samples;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
