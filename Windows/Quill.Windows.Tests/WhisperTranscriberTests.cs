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
