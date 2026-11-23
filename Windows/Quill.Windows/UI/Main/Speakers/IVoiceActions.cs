namespace Quill.Windows.UI;

/// <summary>A speaker of the open meeting as the speakers panel shows it.</summary>
/// <param name="DisplayName">Name the user gave, or null.</param>
/// <param name="SuggestedName">"¿Es Juan?" proposal from the saved voices, or null.</param>
internal sealed record SpeakerPanelItem(string SpeakerId, string? DisplayName, string? SuggestedName);

/// <param name="InPerson">"Reunión presencial" is on for this meeting.</param>
internal sealed record SpeakerPanelData(IReadOnlyList<SpeakerPanelItem> Speakers, bool InPerson)
{
    public static SpeakerPanelData Empty { get; } = new(Array.Empty<SpeakerPanelItem>(), false);
}

/// <summary>A saved voice in "Voces guardadas".</summary>
/// <param name="Name">Null for the owner's own voice.</param>
internal sealed record SavedVoiceItem(string Id, string? Name, bool IsOwner, int Meetings, DateTimeOffset LastSeen);

internal sealed record SavedVoices(IReadOnlyList<SavedVoiceItem> Voices, bool AutoApply);

/// <summary>
/// Optional speaker naming (F11) and in-person mode (F13) for the main window. Naming is never
/// required: the panel only offers it. Everything stays on this machine.
/// </summary>
internal interface IVoiceActions
{
    Task<SpeakerPanelData> LoadAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Names (or, with null/blank, un-names) a speaker; the voice is remembered.</summary>
    Task RenameSpeakerAsync(string sessionId, string speakerId, string? name, CancellationToken cancellationToken);

    Task ConfirmSuggestionAsync(string sessionId, string speakerId, CancellationToken cancellationToken);

    Task RejectSuggestionAsync(string sessionId, string speakerId, CancellationToken cancellationToken);

    /// <summary>Stores the flag and identifies the speakers again. Throws <see cref="AiPackMissingException"/> without the AI pack.</summary>
    Task SetInPersonAsync(string sessionId, bool inPerson, CancellationToken cancellationToken);

    Task<SavedVoices> LoadVoicesAsync(CancellationToken cancellationToken);

    Task RenameVoiceAsync(string voiceId, string name, CancellationToken cancellationToken);

    Task ForgetVoiceAsync(string voiceId, CancellationToken cancellationToken);

    Task SetAutoApplyAsync(bool autoApply, CancellationToken cancellationToken);
}
