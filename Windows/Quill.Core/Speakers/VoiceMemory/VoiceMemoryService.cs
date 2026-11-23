using Quill.Core.Domain;

namespace Quill.Core.Speakers.VoiceMemory;

/// <summary>
/// Speaker-naming memory use cases over voices.json: learning when the user names a speaker,
/// learning the owner's voice, suggestions, and the "Voces guardadas" management. Every
/// read-modify-write is serialized. Everything stays on this machine.
/// </summary>
public sealed class VoiceMemoryService(IVoiceLibraryStore store, VoiceMatchOptions? options = null, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public VoiceMatchOptions Options { get; } = options ?? new VoiceMatchOptions();

    public Task<VoiceLibrary> LoadAsync(CancellationToken cancellationToken) => store.LoadAsync(cancellationToken);

    /// <summary>
    /// The user named (or, with a null/blank name, un-named) <paramref name="speakerId"/>.
    /// "me" is never learned as a named voice: the owner voice is learned separately.
    /// </summary>
    public Task LearnAsync(string sessionId, string speakerId, string? name, float[]? embedding, CancellationToken cancellationToken) =>
        UpdateAsync(library =>
        {
            if (speakerId == SpeakerIds.Me)
            {
                return library;
            }

            return string.IsNullOrWhiteSpace(name) || embedding is null
                ? library.Unlearn(sessionId, speakerId)
                : library.Learn(name, sessionId, speakerId, embedding, _clock.GetUtcNow(), Options);
        }, cancellationToken);

    public Task LearnOwnerAsync(string sessionId, float[] embedding, CancellationToken cancellationToken) =>
        UpdateAsync(library => library.LearnOwner(sessionId, embedding, _clock.GetUtcNow(), Options), cancellationToken);

    public Task ForgetAsync(string voiceId, CancellationToken cancellationToken) =>
        UpdateAsync(library => library.Forget(voiceId), cancellationToken);

    public Task RenameAsync(string voiceId, string name, CancellationToken cancellationToken) =>
        UpdateAsync(library => library.Rename(voiceId, name), cancellationToken);

    public Task MergeAsync(string fromId, string intoId, CancellationToken cancellationToken) =>
        UpdateAsync(library => library.Merge(fromId, intoId), cancellationToken);

    public Task SetAutoApplyAsync(bool autoApply, CancellationToken cancellationToken) =>
        UpdateAsync(library => library.WithAutoApply(autoApply), cancellationToken);

    private async Task UpdateAsync(Func<VoiceLibrary, VoiceLibrary> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var changed = change(library);
            if (!ReferenceEquals(changed, library))
            {
                await store.SaveAsync(changed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
