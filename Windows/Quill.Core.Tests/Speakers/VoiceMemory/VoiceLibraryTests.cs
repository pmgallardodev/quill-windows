using Quill.Core.Speakers.VoiceMemory;
using Xunit;

namespace Quill.Core.Tests.Speakers.VoiceMemory;

public sealed class VoiceLibraryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    // Synthetic 4-d "voices". Juan and Ana are orthogonal; Pedro sits close to Juan.
    private static readonly float[] Juan = [1f, 0f, 0f, 0f];
    private static readonly float[] Ana = [0f, 1f, 0f, 0f];
    private static readonly float[] Owner = [0f, 0f, 1f, 0f];

    private static float[] Near(float[] voice, float noise, int axis = 3)
    {
        var copy = (float[])voice.Clone();
        copy[axis] += noise;
        return copy;
    }

    private static VoiceLibrary JuanAndAna() =>
        VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", Juan, T0)
            .Learn("Ana", "s1", "speaker-2", Ana, T0);

    private static IReadOnlyList<VoiceSuggestion> Suggest(VoiceLibrary library, params (string Id, float[]? Embedding)[] speakers) =>
        library.Suggest(speakers.Select(s => new UnnamedSpeaker(s.Id, s.Embedding)).ToList(), [], [], new VoiceMatchOptions());

    [Fact]
    public void LearningANewNameCreatesAVoicePrint()
    {
        var library = VoiceLibrary.Empty.Learn("Juan", "s1", "speaker-1", [3f, 0f, 0f, 0f], T0);

        var voice = Assert.Single(library.Voices);
        Assert.Equal("Juan", voice.Name);
        Assert.False(voice.IsOwner);
        Assert.Equal(1, voice.Count);
        Assert.Equal(["s1"], voice.Sessions);
        Assert.Equal(T0, voice.LastSeen);
        Assert.Equal([1f, 0f, 0f, 0f], voice.Embedding); // stored normalized
    }

    [Fact]
    public void LearningAKnownNameReinforcesTheRunningMean()
    {
        var library = VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", [1f, 0f, 0f, 0f], T0)
            .Learn("  juan ", "s2", "speaker-3", [0f, 1f, 0f, 0f], T0.AddDays(1));

        var voice = Assert.Single(library.Voices);
        Assert.Equal("Juan", voice.Name);
        Assert.Equal(2, voice.Count);
        Assert.Equal(["s1", "s2"], voice.Sessions);
        Assert.Equal(T0.AddDays(1), voice.LastSeen);
        Assert.Equal([0.5f, 0.5f, 0f, 0f], voice.Embedding);
    }

    [Fact]
    public void RenamingTheSameSpeakerAgainMovesItsSampleInsteadOfDuplicatingIt()
    {
        var library = VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", Juan, T0)
            .Learn("Pedro", "s1", "speaker-1", Juan, T0);

        var voice = Assert.Single(library.Voices);
        Assert.Equal("Pedro", voice.Name);
    }

    [Fact]
    public void UnlearningRemovesTheSampleAndDropsEmptyVoices()
    {
        var library = JuanAndAna().Unlearn("s1", "speaker-1");

        Assert.Equal(["Ana"], library.Voices.Select(v => v.Name));
    }

    [Fact]
    public void SamplesAreCappedKeepingTheMostRecent()
    {
        var options = new VoiceMatchOptions { MaxSamplesPerVoice = 2 };
        var library = VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", Juan, T0, options)
            .Learn("Juan", "s2", "speaker-1", Juan, T0.AddDays(1), options)
            .Learn("Juan", "s3", "speaker-1", Juan, T0.AddDays(2), options);

        Assert.Equal(["s2", "s3"], library.Voices.Single().Sessions);
    }

    [Fact]
    public void AClearMatchIsSuggested()
    {
        var suggestion = Assert.Single(Suggest(JuanAndAna(), ("speaker-1", Near(Juan, 0.2f))));

        Assert.Equal("speaker-1", suggestion.SpeakerId);
        Assert.Equal("Juan", suggestion.Name);
        Assert.True(suggestion.Similarity > 0.95);
    }

    [Fact]
    public void AWeakMatchIsNotSuggested()
    {
        // cos ≈ 0.55: same direction-ish, but below the conservative threshold.
        Assert.Empty(Suggest(JuanAndAna(), ("speaker-1", [1f, 0f, 0f, 1.5f])));
    }

    [Fact]
    public void TwoSimilarVoicesAreAmbiguousAndNothingIsSuggested()
    {
        var library = VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", Juan, T0)
            .Learn("Pedro", "s1", "speaker-2", [0.97f, 0.24f, 0f, 0f], T0);

        // Halfway between both: each scores high, margin is tiny.
        Assert.Empty(Suggest(library, ("speaker-1", [0.99f, 0.12f, 0f, 0f])));
    }

    [Fact]
    public void TheOwnersVoiceCompetesSoAnOwnerLikeSpeakerGetsNoName()
    {
        var library = VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", [0.9f, 0f, 0.44f, 0f], T0)
            .LearnOwner("s2", Owner, T0);

        // Closer to the owner than to Juan: never suggest a stranger's name for it.
        Assert.Empty(Suggest(library, ("speaker-1", [0.5f, 0f, 0.86f, 0f])));
    }

    [Fact]
    public void TheOwnerVoiceIsNeverSuggestedAsAName()
    {
        var library = VoiceLibrary.Empty.LearnOwner("s1", Owner, T0);

        Assert.Empty(Suggest(library, ("speaker-1", Owner)));
    }

    [Fact]
    public void SpeakersWithoutEmbeddingOrWithOtherDimensionsGetNothing()
    {
        Assert.Empty(Suggest(JuanAndAna(), ("speaker-1", null), ("speaker-2", [1f, 0f])));
    }

    [Fact]
    public void OneVoiceGoesToOneSpeakerOnlyTheClosestOne()
    {
        var suggestions = Suggest(JuanAndAna(), ("speaker-1", Near(Juan, 0.4f)), ("speaker-2", Near(Juan, 0.1f)));

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("speaker-2", suggestion.SpeakerId);
    }

    [Fact]
    public void RejectedVoicesAndNamesAlreadyUsedInTheMeetingAreSkipped()
    {
        var library = JuanAndAna();
        var juanId = library.Voices.Single(v => v.Name == "Juan").Id;
        var speakers = new[] { new UnnamedSpeaker("speaker-1", Juan), new UnnamedSpeaker("speaker-2", Ana) };

        var rejected = library.Suggest(speakers, [new VoiceRejection("speaker-1", juanId)], [], new VoiceMatchOptions());
        Assert.Equal(["speaker-2"], rejected.Select(s => s.SpeakerId));

        var inUse = library.Suggest(speakers, [], ["ana"], new VoiceMatchOptions());
        Assert.Equal(["speaker-1"], inUse.Select(s => s.SpeakerId));
    }

    [Fact]
    public void ForgetRemovesAVoice()
    {
        var library = JuanAndAna();
        var juanId = library.Voices.Single(v => v.Name == "Juan").Id;

        Assert.Equal(["Ana"], library.Forget(juanId).Voices.Select(v => v.Name));
    }

    [Fact]
    public void RenameChangesTheNameAndMergesIntoAnExistingNameWithTheSameSpelling()
    {
        var library = JuanAndAna();
        var juanId = library.Voices.Single(v => v.Name == "Juan").Id;

        Assert.Contains(library.Rename(juanId, "Juan Pérez").Voices, v => v.Id == juanId && v.Name == "Juan Pérez");

        var merged = library.Rename(juanId, "ANA");
        var ana = Assert.Single(merged.Voices);
        Assert.Equal("Ana", ana.Name);
        Assert.Equal(2, ana.Count);
    }

    [Fact]
    public void MergeFoldsSamplesIntoTheKeptVoice()
    {
        var library = JuanAndAna();
        var juanId = library.Voices.Single(v => v.Name == "Juan").Id;
        var anaId = library.Voices.Single(v => v.Name == "Ana").Id;

        var merged = library.Merge(juanId, anaId);

        var ana = Assert.Single(merged.Voices);
        Assert.Equal(anaId, ana.Id);
        Assert.Equal(2, ana.Count);
        Assert.Equal([0.5f, 0.5f, 0f, 0f], ana.Embedding);
    }

    [Fact]
    public void OwnerVoiceIsLearnedPerSessionUntilItHasEnoughSessions()
    {
        var options = new VoiceMatchOptions { OwnerLearningSessions = 2 };
        var library = VoiceLibrary.Empty;
        Assert.True(library.NeedsOwnerSamples(options));

        library = library.LearnOwner("s1", Owner, T0, options).LearnOwner("s1", Owner, T0, options);
        Assert.Equal(1, library.Owner!.Count);
        Assert.True(library.NeedsOwnerSamples(options));

        library = library.LearnOwner("s2", Near(Owner, 0.1f), T0, options);
        Assert.Equal(2, library.Owner!.Count);
        Assert.False(library.NeedsOwnerSamples(options));
        Assert.Null(library.Owner.Name);
        Assert.True(library.Owner.IsOwner);
    }

    [Fact]
    public void AutoApplyIsOffByDefault()
    {
        Assert.False(VoiceLibrary.Empty.AutoApply);
        Assert.True(VoiceLibrary.Empty.WithAutoApply(true).AutoApply);
    }
}
