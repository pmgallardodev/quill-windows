using NAudio.Wave;
using Quill.Windows.Transcription;
using Xunit;

namespace Quill.Windows.Tests;

public class WhisperTranscriberTests
{
    [Fact]
    public void NormalizeToWhisperWav_ReturnsSourceDuration_AndWrites16kHzMono()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.wav");
        var output = Path.Combine(temp.Path, "normalized.wav");
        WriteSilence(input, new WaveFormat(44_100, 16, 2), TimeSpan.FromSeconds(2));

        var duration = WhisperTranscriber.NormalizeToWhisperWav(input, output);

        Assert.Equal(2d, duration.TotalSeconds, precision: 1);

        using var reader = new WaveFileReader(output);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(2d, reader.TotalTime.TotalSeconds, precision: 1);
    }

    [Fact]
    public void NormalizeToWhisperWav_AcceptsMonoInput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.wav");
        var output = Path.Combine(temp.Path, "normalized.wav");
        WriteSilence(input, new WaveFormat(48_000, 16, 1), TimeSpan.FromSeconds(1));

        var duration = WhisperTranscriber.NormalizeToWhisperWav(input, output);

        Assert.Equal(1d, duration.TotalSeconds, precision: 1);
        using var reader = new WaveFileReader(output);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void NormalizeToWhisperWav_DownmixesSurroundInput(int channels)
    {
        // Un microfono o un loopback 5.1/7.1 dejaba la sesion entera sin transcribir.
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.wav");
        var output = Path.Combine(temp.Path, "normalized.wav");
        WriteConstant(input, new WaveFormat(48_000, 16, channels), TimeSpan.FromSeconds(1), 0.5f);

        var duration = WhisperTranscriber.NormalizeToWhisperWav(input, output);

        Assert.Equal(1d, duration.TotalSeconds, precision: 1);

        using var reader = new WaveFileReader(output);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1d, reader.TotalTime.TotalSeconds, precision: 1);
    }

    [Fact]
    public void NormalizeToWhisperWav_AveragesTheChannelsInsteadOfSummingThem()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.wav");
        var output = Path.Combine(temp.Path, "normalized.wav");
        WriteConstant(input, new WaveFormat(48_000, 16, 6), TimeSpan.FromSeconds(1), 0.5f);

        WhisperTranscriber.NormalizeToWhisperWav(input, output);

        // Los extremos los suaviza el remuestreo, asi que se mira el centro de la senal.
        var samples = ReadSamples(output);
        Assert.All(samples[4_000..12_000], sample => Assert.Equal(0.5f, sample, tolerance: 0.01f));
    }

    private static float[] ReadSamples(string path)
    {
        using var reader = new AudioFileReader(path);
        var samples = new float[reader.Length / sizeof(float)];
        var total = 0;
        int read;
        while ((read = reader.Read(samples, total, samples.Length - total)) > 0)
        {
            total += read;
        }

        return samples[..total];
    }

    /// <summary>Todos los canales con el mismo valor: promediarlos debe devolverlo intacto.</summary>
    private static void WriteConstant(string path, WaveFormat format, TimeSpan duration, float value)
    {
        using var writer = new WaveFileWriter(path, format);
        var frames = (int)(format.SampleRate * duration.TotalSeconds);
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < format.Channels; channel++)
            {
                writer.WriteSample(value);
            }
        }
    }

    private static void WriteSilence(string path, WaveFormat format, TimeSpan duration)
    {
        using var writer = new WaveFileWriter(path, format);
        var buffer = new byte[format.AverageBytesPerSecond];
        var remaining = (long)(format.AverageBytesPerSecond * duration.TotalSeconds);
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            writer.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }
}
