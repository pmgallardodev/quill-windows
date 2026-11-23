using Quill.Core.Domain;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>
/// Resolves the label shown for a speaker id: the display name from speakers.json when
/// the user named the speaker, otherwise "Yo", "Interlocutor N", or "Otros" for the v1
/// "them" label of sessions recorded before speaker separation.
/// </summary>
internal sealed class SpeakerLabels
{
    private readonly Dictionary<string, string> _names;

    public SpeakerLabels(IEnumerable<Speaker> speakers)
    {
        _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var speaker in speakers)
        {
            if (!string.IsNullOrWhiteSpace(speaker.Id) && !string.IsNullOrWhiteSpace(speaker.DisplayName))
            {
                _names[speaker.Id.Trim()] = speaker.DisplayName.Trim();
            }
        }
    }

    public static SpeakerLabels Empty { get; } = new(Array.Empty<Speaker>());

    public string Label(string? speakerId)
    {
        if (speakerId is not null && _names.TryGetValue(speakerId.Trim(), out var name))
        {
            return name;
        }

        return DefaultLabel(speakerId);
    }

    /// <summary>Label of a speaker nobody named.</summary>
    public static string DefaultLabel(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            return Loc.Get("Speaker_Unknown");
        }

        var id = speakerId.Trim();
        if (string.Equals(id, SpeakerIds.Me, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.Get("Speaker_Me");
        }

        if (string.Equals(id, SpeakerIds.LegacyThem, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.Get("Speaker_Others");
        }

        if (SpeakerIds.IsRemote(id) &&
            int.TryParse(id.AsSpan("speaker-".Length), out var number) &&
            number > 0)
        {
            return Loc.Format("Speaker_Numbered", number);
        }

        return id;
    }
}
