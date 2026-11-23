using Quill.Core.Domain;
using Quill.Core.Speakers.VoiceMemory;
using Quill.Windows.UI;

namespace Quill.Windows.Speakers;

/// <summary>The main window's <see cref="IVoiceActions"/> over <see cref="SpeakersModule"/>.</summary>
internal sealed class SpeakersVoiceActions(SpeakersModule speakers) : IVoiceActions
{
    private readonly SpeakersModule _speakers = speakers ?? throw new ArgumentNullException(nameof(speakers));

    private VoiceMemoryService Voices =>
        _speakers.Voices ?? throw new InvalidOperationException("Voice memory is not configured.");

    public async Task<SpeakerPanelData> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var profiles = await _speakers.LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var state = await _speakers.LoadStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var items = profiles
            .Where(profile => profile.Id != SpeakerIds.Me)
            .Select(profile => new SpeakerPanelItem(
                profile.Id,
                profile.DisplayName,
                profile.DisplayName is null
                    ? state.Suggestions.FirstOrDefault(suggestion => suggestion.SpeakerId == profile.Id)?.Name
                    : null))
            .ToList();
        return new SpeakerPanelData(items, state.InPerson);
    }

    public Task RenameSpeakerAsync(string sessionId, string speakerId, string? name, CancellationToken cancellationToken) =>
        _speakers.RenameAsync(sessionId, speakerId, name, cancellationToken);

    public Task ConfirmSuggestionAsync(string sessionId, string speakerId, CancellationToken cancellationToken) =>
        _speakers.ConfirmSuggestionAsync(sessionId, speakerId, cancellationToken);

    public Task RejectSuggestionAsync(string sessionId, string speakerId, CancellationToken cancellationToken) =>
        _speakers.RejectSuggestionAsync(sessionId, speakerId, cancellationToken);

    public async Task SetInPersonAsync(string sessionId, bool inPerson, CancellationToken cancellationToken)
    {
        var result = await _speakers.SetInPersonAsync(sessionId, inPerson, cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (result.State)
        {
            case SpeakersResolveState.Resolved:
                return;
            case SpeakersResolveState.Unavailable:
                throw new AiPackMissingException();
            case SpeakersResolveState.NoTranscript:
                throw new InvalidOperationException(Resources.Loc.Get("Pipeline_NoTranscript"));
            default:
                throw new InvalidOperationException(Resources.Loc.Get("Pipeline_SessionNotFound"));
        }
    }

    public async Task<SavedVoices> LoadVoicesAsync(CancellationToken cancellationToken)
    {
        var library = await Voices.LoadAsync(cancellationToken).ConfigureAwait(false);
        var voices = library.Named
            .Concat(library.Owner is { } owner ? new[] { owner } : Array.Empty<VoicePrint>())
            .Select(voice => new SavedVoiceItem(voice.Id, voice.Name, voice.IsOwner, voice.Sessions.Count, voice.LastSeen))
            .ToList();
        return new SavedVoices(voices, library.AutoApply);
    }

    public Task RenameVoiceAsync(string voiceId, string name, CancellationToken cancellationToken) =>
        Voices.RenameAsync(voiceId, name, cancellationToken);

    public Task ForgetVoiceAsync(string voiceId, CancellationToken cancellationToken) =>
        Voices.ForgetAsync(voiceId, cancellationToken);

    public Task SetAutoApplyAsync(bool autoApply, CancellationToken cancellationToken) =>
        Voices.SetAutoApplyAsync(autoApply, cancellationToken);
}
