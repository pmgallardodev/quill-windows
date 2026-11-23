namespace Quill.Core.Domain;

/// <summary>
/// Physical audio track a segment was captured on. Stored on disk as "mic" /
/// "system" (see <see cref="TrackNames"/>), matching the meta.json file keys.
/// </summary>
public enum Track
{
    /// <summary>Local microphone (mic.wav). Usually the owner, but never assume "mic = one person".</summary>
    Microphone,

    /// <summary>System loopback (system.wav): remote participants.</summary>
    System,
}

/// <summary>On-disk names of the tracks (meta.json "files" keys, transcript "track" field).</summary>
public static class TrackNames
{
    public const string Microphone = "mic";
    public const string System = "system";

    public static string ToName(Track track) => track == Track.Microphone ? Microphone : System;

    public static bool TryParse(string? name, out Track track)
    {
        if (string.Equals(name, Microphone, StringComparison.OrdinalIgnoreCase))
        {
            track = Track.Microphone;
            return true;
        }

        if (string.Equals(name, System, StringComparison.OrdinalIgnoreCase))
        {
            track = Track.System;
            return true;
        }

        track = default;
        return false;
    }
}

/// <summary>Well-known speaker ids and helpers.</summary>
public static class SpeakerIds
{
    /// <summary>The owner of the machine.</summary>
    public const string Me = "me";

    /// <summary>v1 label for everything on the system track. Still read, never written by v2 code.</summary>
    public const string LegacyThem = "them";

    private const string RemotePrefix = "speaker-";

    /// <summary>Id for the n-th (1-based) diarized speaker, e.g. "speaker-1".</summary>
    public static string Remote(int number) =>
        number >= 1
            ? $"{RemotePrefix}{number}"
            : throw new ArgumentOutOfRangeException(nameof(number), number, "speaker numbers start at 1");

    public static bool IsRemote(string? speakerId) =>
        speakerId is not null && speakerId.StartsWith(RemotePrefix, StringComparison.Ordinal);

    /// <summary>Track implied by a v1 speaker label: "me" is the microphone, anything else the system track.</summary>
    public static Track LegacyTrack(string speakerId) =>
        string.Equals(speakerId, Me, StringComparison.OrdinalIgnoreCase) ? Track.Microphone : Track.System;
}

/// <summary>
/// One transcribed utterance. Times are offsets from the session start (the
/// earliest track), which is also what summary citations (<c>at</c>) refer to.
/// </summary>
/// <param name="Start">Inclusive start offset.</param>
/// <param name="End">End offset, never before <paramref name="Start"/>.</param>
/// <param name="Text">Recognized (or user-corrected) text.</param>
/// <param name="SpeakerId">"me", "speaker-N", or "them" for v1 sessions.</param>
/// <param name="Track">Track the audio came from, independent of who spoke.</param>
/// <param name="Editable">False for provisional segments (live pass) the user cannot edit.</param>
/// <param name="Edited">True when the user changed the text after transcription.</param>
public sealed record TranscriptSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string SpeakerId,
    Track Track,
    bool Editable = true,
    bool Edited = false)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>Whether <paramref name="at"/> falls inside [Start - tolerance, End + tolerance].</summary>
    public bool Contains(TimeSpan at, TimeSpan tolerance = default) =>
        at >= Start - tolerance && at <= End + tolerance;
}

/// <summary>Which transcription pass produced a transcript.</summary>
public enum TranscriptPass
{
    /// <summary>Final high-quality pass (large-v3-turbo), stored in transcript.json.</summary>
    Final,

    /// <summary>Provisional live pass (whisper small), stored in live/transcript.json.</summary>
    Live,
}
