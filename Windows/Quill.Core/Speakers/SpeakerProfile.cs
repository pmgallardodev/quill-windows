using Quill.Core.Domain;

namespace Quill.Core.Speakers;

/// <summary>
/// A resolved participant: the <see cref="Speaker"/> stored in speakers.json plus the
/// data kept in speakers.resolution.json (colour slot and voice embedding, reused by
/// speaker-naming memory F11 and owner voice recognition F13).
/// </summary>
/// <param name="ColorSlot">0 for "me", N for "speaker-N". The UI maps slots onto its palette.</param>
/// <param name="Embedding">Cluster centroid from diarization, or null when none was computed.</param>
public sealed record SpeakerProfile(string Id, Track Track, string? DisplayName, int ColorSlot, float[]? Embedding)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName.Trim();

    public Speaker ToSpeaker() => new(Id, Track, DisplayName);

    /// <summary>Default colour slot of an id: "me" 0, "speaker-N" N, anything else (v1 "them") 1.</summary>
    public static int DefaultColorSlot(string speakerId) =>
        speakerId == SpeakerIds.Me ? 0 : RemoteNumber(speakerId) ?? 1;

    /// <summary>N of "speaker-N", or null for any other id.</summary>
    public static int? RemoteNumber(string speakerId) =>
        SpeakerIds.IsRemote(speakerId) && int.TryParse(speakerId.AsSpan("speaker-".Length), out var number) && number >= 1
            ? number
            : null;

    /// <summary>Ids that may be written by v2 code: "me" or "speaker-N".</summary>
    public static bool IsAssignable(string? speakerId) =>
        speakerId == SpeakerIds.Me || (speakerId is not null && RemoteNumber(speakerId) is not null);
}

/// <summary>A user correction, replayed in order after every automatic resolution.</summary>
public abstract record SpeakerEdit;

/// <summary>Every segment of <paramref name="From"/> now belongs to <paramref name="Into"/>; <paramref name="From"/> disappears.</summary>
public sealed record MergeSpeakers(string From, string Into) : SpeakerEdit;

/// <summary>
/// The segment on <paramref name="Track"/> starting at <paramref name="Start"/> belongs to
/// <paramref name="SpeakerId"/>. Keyed by time, not index, so it survives echo removal.
/// </summary>
public sealed record ReassignSegment(Track Track, TimeSpan Start, string SpeakerId) : SpeakerEdit;
