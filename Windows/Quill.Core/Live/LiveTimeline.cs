using Quill.Core.Domain;

namespace Quill.Core.Live;

/// <summary>
/// Maps each track's first captured buffer to an offset from the session start, mirroring
/// meta.json <c>startOffsetMs</c>. The first track seen defines the origin; a track whose first
/// buffer is (by clock jitter) slightly earlier is clamped to zero. Offsets are fixed on first
/// sighting, so live timestamps never jump. Thread-safe.
/// </summary>
public sealed class LiveTimeline
{
    private readonly object _lock = new();
    private readonly Dictionary<Track, TimeSpan> _offsets = new();
    private DateTimeOffset? _origin;

    public TimeSpan OffsetOf(Track track, DateTimeOffset trackStartedAt)
    {
        lock (_lock)
        {
            if (_offsets.TryGetValue(track, out var known))
            {
                return known;
            }

            _origin ??= trackStartedAt;
            var offset = trackStartedAt - _origin.Value;
            if (offset < TimeSpan.Zero)
            {
                offset = TimeSpan.Zero;
            }

            _offsets[track] = offset;
            return offset;
        }
    }
}
