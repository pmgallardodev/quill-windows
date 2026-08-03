namespace Quill.Windows.Sessions;

/// <summary>
/// Cuanto habla cada lado y donde. Todo sale de transcript.json, que ya se
/// escribe: no hace falta capturar ni calcular nada nuevo durante la grabacion.
/// </summary>
internal static class SessionInsights
{
    public const string MicrophoneSpeaker = "me";
    public const string SystemSpeaker = "them";

    /// <summary>
    /// Los segmentos de las dos pistas se solapan cuando hablan a la vez, asi que
    /// sumar duraciones daria mas del total de la reunion. Cada pista se fusiona
    /// consigo misma antes de sumar y los porcentajes se reparten sobre el habla,
    /// no sobre la duracion de la sesion, que incluye los silencios.
    /// </summary>
    public static ParticipationSummary Summarize(IReadOnlyList<TranscriptSegment> segments)
    {
        var microphone = SpeakingTime(segments, MicrophoneSpeaker);
        var system = SpeakingTime(segments, SystemSpeaker);
        return new ParticipationSummary(microphone, system);
    }

    /// <summary>
    /// Una pista sin una sola palabra casi siempre es un fallo, no una reunion en
    /// la que alguien callo una hora: el micro silenciado o el loopback apuntando
    /// al dispositivo equivocado. Nada lo indicaba hasta abrir el transcript.
    /// </summary>
    public static IReadOnlyList<string> SilentTracks(IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var silent = new List<string>();
        if (SpeakingTime(segments, MicrophoneSpeaker) <= TimeSpan.Zero)
        {
            silent.Add(MicrophoneSpeaker);
        }

        if (SpeakingTime(segments, SystemSpeaker) <= TimeSpan.Zero)
        {
            silent.Add(SystemSpeaker);
        }

        return silent;
    }

    /// <summary>
    /// Tiempo real de habla de una pista, sin contar dos veces los segmentos que
    /// se solapan entre si.
    /// </summary>
    public static TimeSpan SpeakingTime(IReadOnlyList<TranscriptSegment> segments, string speaker)
    {
        var ranges = segments
            .Where(segment => string.Equals(segment.Speaker, speaker, StringComparison.OrdinalIgnoreCase))
            .Select(segment => (Start: Math.Max(0, segment.StartMs), End: Math.Max(0, segment.EndMs)))
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ToList();

        if (ranges.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var total = 0L;
        var currentStart = ranges[0].Start;
        var currentEnd = ranges[0].End;

        foreach (var range in ranges.Skip(1))
        {
            if (range.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, range.End);
                continue;
            }

            total += currentEnd - currentStart;
            currentStart = range.Start;
            currentEnd = range.End;
        }

        return TimeSpan.FromMilliseconds(total + currentEnd - currentStart);
    }

    /// <summary>
    /// Reparte la reunion en tramos iguales y dice quien hablaba en cada uno, para
    /// dibujar una barra donde localizar de un vistazo la parte que se busca.
    /// </summary>
    public static IReadOnlyList<TimelineSlot> Timeline(
        IReadOnlyList<TranscriptSegment> segments,
        int slots)
    {
        if (segments.Count == 0 || slots <= 0)
        {
            return Array.Empty<TimelineSlot>();
        }

        var lastMs = segments.Max(segment => Math.Max(segment.StartMs, segment.EndMs));
        if (lastMs <= 0)
        {
            return Array.Empty<TimelineSlot>();
        }

        var width = (double)lastMs / slots;
        var microphone = new bool[slots];
        var system = new bool[slots];

        foreach (var segment in segments)
        {
            var start = Math.Max(0, segment.StartMs);
            var end = Math.Max(0, segment.EndMs);
            if (end <= start)
            {
                continue;
            }

            var target = string.Equals(segment.Speaker, MicrophoneSpeaker, StringComparison.OrdinalIgnoreCase)
                ? microphone
                : string.Equals(segment.Speaker, SystemSpeaker, StringComparison.OrdinalIgnoreCase)
                    ? system
                    : null;
            if (target is null)
            {
                continue;
            }

            var from = (int)Math.Clamp(start / width, 0, slots - 1);
            // End is exclusive: a segment ending at a slot boundary must not
            // illuminate the following slot.
            var to = (int)Math.Clamp(Math.Ceiling(end / width) - 1, 0, slots - 1);

            for (var index = from; index <= to; index++)
            {
                target[index] = true;
            }
        }

        return Enumerable.Range(0, slots)
            .Select(index => new TimelineSlot(
                TimeSpan.FromMilliseconds(index * width),
                microphone[index],
                system[index]))
            .ToList();
    }
}

internal sealed record ParticipationSummary(TimeSpan Microphone, TimeSpan System)
{
    public TimeSpan Total => Microphone + System;

    /// <summary>Sobre el habla total, no sobre la duracion: los silencios no son de nadie.</summary>
    public double MicrophoneShare => Total > TimeSpan.Zero ? Microphone / Total : 0d;

    public double SystemShare => Total > TimeSpan.Zero ? System / Total : 0d;

    public bool HasSpeech => Total > TimeSpan.Zero;
}

internal sealed record TimelineSlot(TimeSpan At, bool Microphone, bool System);
