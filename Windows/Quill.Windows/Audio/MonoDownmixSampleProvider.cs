using NAudio.Wave;

namespace Quill.Windows.Audio;

/// <summary>
/// Promedia cualquier numero de canales a uno solo. NAudio solo trae
/// StereoToMonoSampleProvider, que exige exactamente dos, de modo que un microfono
/// o un loopback 5.1/7.1 dejaba la pista sin convertir a lo que consume Whisper.
/// </summary>
internal sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _interleaved = Array.Empty<float>();

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_channels == 1)
        {
            return _source.Read(buffer, offset, count);
        }

        var required = count * _channels;
        if (_interleaved.Length < required)
        {
            _interleaved = new float[required];
        }

        var read = _source.Read(_interleaved, 0, required);
        // Los proveedores de NAudio devuelven frames completos; un resto parcial solo
        // aparece si el origen incumple eso y se descarta antes que emitir un frame
        // promediado con canales que no se han leido.
        var frames = read / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var start = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += _interleaved[start + channel];
            }

            buffer[offset + frame] = sum / _channels;
        }

        return frames;
    }
}
