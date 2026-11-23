namespace Quill.Core.Domain;

/// <summary>
/// A recorded meeting. <see cref="Id"/> is the session folder name and is the key
/// every <see cref="Ports.ISessionStore"/> method takes.
/// </summary>
/// <param name="Id">Folder name, e.g. "2026-11-23_10-00-00".</param>
/// <param name="Folder">Absolute path of the session folder.</param>
/// <param name="Title">User-given title, or null (UI falls back to <see cref="Id"/>).</param>
/// <param name="Started">Wall-clock start.</param>
/// <param name="Ended">Wall-clock end; null while recording or for an interrupted capture.</param>
/// <param name="Tracks">Audio tracks present, with their file names and start offsets.</param>
/// <param name="Markers">Markers placed during recording.</param>
public sealed record Session(
    string Id,
    string Folder,
    string? Title,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    IReadOnlyList<SessionTrack> Tracks,
    IReadOnlyList<Marker> Markers)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Id : Title.Trim();

    public TimeSpan Duration => Ended is { } ended && ended > Started ? ended - Started : TimeSpan.Zero;
}

/// <param name="Track">Which track.</param>
/// <param name="FileName">WAV file name relative to the session folder (e.g. "mic.wav").</param>
/// <param name="StartOffset">Offset of this track's first sample from the session start.</param>
public sealed record SessionTrack(Track Track, string FileName, TimeSpan StartOffset);

/// <summary>
/// A participant label. Ids are stable within a session ("me", "speaker-1");
/// <see cref="DisplayName"/> is the optional, user-given name.
/// </summary>
public sealed record Speaker(string Id, Track Track, string? DisplayName = null)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName.Trim();
}

/// <summary>A point of interest placed by the user during or after recording.</summary>
public sealed record Marker(TimeSpan At, string? Label = null);

/// <summary>
/// A user note. <see cref="At"/> is set when the note was taken during recording
/// (it then steers the summary to that moment); null for free-form notes.
/// </summary>
public sealed record Note(string Text, TimeSpan? At = null);
