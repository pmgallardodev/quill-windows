using NAudio.Wave;
using Quill.Core.Ports;
using Quill.Core.Speakers;
using Quill.Windows.Audio;

namespace Quill.Windows.Adapters.Speakers;

/// <summary>
/// RMS per 10 ms frame of a track, padded with silent frames for the track's start
/// offset so frame 0 is the session start (what <see cref="CrossTrackEchoClassifier"/> expects).
/// Works at the file's native rate: no resampling needed for an energy envelope.
/// </summary>
internal sealed class NAudioEnergyEnvelopeReader : IEnergyEnvelopeReader
{
    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(10);

    public Task<EnergyEnvelope?> ReadAsync(string audioPath, TimeSpan startOffset, CancellationToken cancellationToken) =>
        Task.Run(() => Read(audioPath, startOffset, cancellationToken), cancellationToken);

    private static EnergyEnvelope? Read(string audioPath, TimeSpan startOffset, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new AudioFileReader(audioPath);
            ISampleProvider source = reader.WaveFormat.Channels > 1 ? new MonoDownmixSampleProvider(reader) : reader;
            var frameSamples = Math.Max(1, (int)(source.WaveFormat.SampleRate * FrameDuration.TotalSeconds));
            var rms = new List<float>();
            for (var i = 0; i < (int)(startOffset.Ticks / FrameDuration.Ticks); i++)
            {
                rms.Add(0);
            }

            var buffer = new float[frameSamples];
            int read;
            while ((read = ReadFull(source, buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sum = 0;
                for (var i = 0; i < read; i++)
                {
                    sum += buffer[i] * buffer[i];
                }

                rms.Add((float)Math.Sqrt(sum / read));
            }

            return new EnergyEnvelope(FrameDuration, rms);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int ReadFull(ISampleProvider source, float[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = source.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
