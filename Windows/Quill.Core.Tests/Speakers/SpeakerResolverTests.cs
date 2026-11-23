using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Speakers;
using Xunit;

namespace Quill.Core.Tests.Speakers;

public sealed class SpeakerResolverTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TranscriptSegment Mic(double start, double end, string text, bool edited = false) =>
        new(S(start), S(end), text, SpeakerIds.Me, Track.Microphone, Edited: edited);

    private static TranscriptSegment Sys(double start, double end, string text) =>
        new(S(start), S(end), text, SpeakerIds.LegacyThem, Track.System);

    private static SpeakerTurn Turn(double start, double end, int cluster) => new(S(start), S(end), cluster);

    private static DiarizationResult Diarization(Track track, IReadOnlyList<SpeakerTurn> turns, Dictionary<int, float[]>? embeddings = null) =>
        new(track, turns, embeddings ?? new Dictionary<int, float[]>());

    private static EchoVerdict Genuine(int index) => new(index, false, 0.9);

    private static EchoVerdict Echo(int index) => new(index, true, 0.9);

    [Fact]
    public void SystemClustersAreNumberedByFirstAppearance()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Sys(0, 4, "first remote"), Sys(5, 9, "second remote"), Sys(10, 14, "first again")],
            [],
            Diarization(Track.System, [Turn(0, 4, 7), Turn(5, 9, 2), Turn(10, 14, 7)])));

        Assert.Equal(["speaker-1", "speaker-2", "speaker-1"], resolved.Segments.Select(s => s.SpeakerId));
        Assert.All(resolved.Segments, s => Assert.Equal(Track.System, s.Track));
    }

    [Fact]
    public void GenuineMicSpeechIsMe()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Mic(0, 3, "hello everyone"), Sys(4, 8, "hi")],
            [Genuine(0)],
            Diarization(Track.System, [Turn(4, 8, 0)])));

        Assert.Equal([SpeakerIds.Me, "speaker-1"], resolved.Segments.Select(s => s.SpeakerId));
    }

    [Fact]
    public void EchoDuplicatingASystemSegmentIsRemoved()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Sys(1, 6, "we need the budget by Monday"), Mic(1.2, 6.1, "we need the budget by Monday"), Mic(8, 10, "sure, I will send it")],
            [Echo(0), Genuine(1)],
            Diarization(Track.System, [Turn(1, 6, 0)])));

        Assert.Equal(2, resolved.Segments.Count);
        Assert.Equal(["speaker-1", SpeakerIds.Me], resolved.Segments.Select(s => s.SpeakerId));
        Assert.Equal(1, resolved.EchoSegmentsRemoved);
        Assert.Equal(0, resolved.EchoSegmentsReattributed);
    }

    [Fact]
    public void EchoWithoutMatchingSystemTextIsReattributedToTheOverlappingRemoteSpeaker()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Sys(0, 3, "intro"), Sys(4, 9, "something else entirely"), Mic(4.2, 8.8, "garbled leaked words")],
            [Echo(0)],
            Diarization(Track.System, [Turn(0, 3, 0), Turn(4, 9, 1)])));

        var reattributed = resolved.Segments.Single(s => s.Track == Track.Microphone);
        Assert.Equal("speaker-2", reattributed.SpeakerId);
        Assert.Equal(1, resolved.EchoSegmentsReattributed);
    }

    [Fact]
    public void EditedEchoSegmentIsReattributedNotRemoved()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Sys(1, 6, "we need the budget by Monday"), Mic(1.2, 6.1, "we need the budget by Monday", edited: true)],
            [Echo(0)],
            Diarization(Track.System, [Turn(1, 6, 0)])));

        Assert.Equal(2, resolved.Segments.Count);
        Assert.All(resolved.Segments, s => Assert.Equal("speaker-1", s.SpeakerId));
    }

    [Fact]
    public void VeryShortClusterIsMergedIntoTheMostSimilarCluster()
    {
        var embeddings = new Dictionary<int, float[]>
        {
            [0] = [1f, 0f],
            [1] = [0f, 1f],
            [2] = [0.1f, 0.99f], // 0.5 s blip that sounds like cluster 1
        };

        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Sys(0, 5, "a"), Sys(6, 11, "b"), Sys(12, 12.5, "c")],
            [],
            Diarization(Track.System, [Turn(0, 5, 0), Turn(6, 11, 1), Turn(12, 12.5, 2)], embeddings)));

        Assert.Equal(["speaker-1", "speaker-2", "speaker-2"], resolved.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["speaker-1", "speaker-2"], resolved.Speakers.Select(p => p.Id));
    }

    [Fact]
    public void ProfilesCarryTrackColorSlotAndClusterEmbedding()
    {
        var embeddings = new Dictionary<int, float[]> { [3] = [0.6f, 0.8f] };
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Mic(0, 2, "hello there friends"), Sys(3, 6, "hi")],
            [Genuine(0)],
            Diarization(Track.System, [Turn(3, 6, 3)], embeddings)));

        Assert.Collection(
            resolved.Speakers,
            me =>
            {
                Assert.Equal(SpeakerIds.Me, me.Id);
                Assert.Equal(Track.Microphone, me.Track);
                Assert.Equal(0, me.ColorSlot);
                Assert.Null(me.DisplayName);
            },
            remote =>
            {
                Assert.Equal("speaker-1", remote.Id);
                Assert.Equal(Track.System, remote.Track);
                Assert.Equal(1, remote.ColorSlot);
                Assert.Equal([0.6f, 0.8f], remote.Embedding!);
            });
    }

    [Fact]
    public void SystemSegmentsWithoutTurnsStillGetARemoteSpeaker()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Sys(0, 4, "silence for the diarizer")],
            [],
            Diarization(Track.System, [])));

        Assert.Equal("speaker-1", Assert.Single(resolved.Segments).SpeakerId);
    }

    [Fact]
    public void MicDiarizationKeepsTheDominantVoiceAsMeAndNumbersTheOthers()
    {
        // In-person mode (F13): the mic track holds more than one person.
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Mic(0, 10, "long owner turn here"), Sys(11, 14, "remote"), Mic(15, 18, "colleague in the room")],
            [Genuine(0), Genuine(1)],
            Diarization(Track.System, [Turn(11, 14, 0)]),
            Diarization(Track.Microphone, [Turn(0, 10, 4), Turn(15, 18, 9)])));

        Assert.Equal([SpeakerIds.Me, "speaker-1", "speaker-2"], resolved.Segments.Select(s => s.SpeakerId));
        Assert.Equal(Track.Microphone, resolved.Speakers.Single(p => p.Id == "speaker-2").Track);
    }

    [Fact]
    public void EditsReplayMergeAndReassignInOrder()
    {
        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            [Mic(0, 2, "hello there everyone"), Sys(3, 5, "a"), Sys(6, 8, "b"), Sys(9, 11, "c")],
            [Genuine(0)],
            Diarization(Track.System, [Turn(3, 5, 0), Turn(6, 8, 1), Turn(9, 11, 2)])));

        var edited = SpeakerEdits.Apply(resolved.Segments, resolved.Speakers,
        [
            new MergeSpeakers(From: "speaker-3", Into: "speaker-1"),
            new ReassignSegment(Track.System, S(6), SpeakerIds.Me),
            new MergeSpeakers(From: "speaker-404", Into: "speaker-1"), // no-op
        ]);

        Assert.Equal([SpeakerIds.Me, "speaker-1", SpeakerIds.Me, "speaker-1"], edited.Segments.Select(s => s.SpeakerId));
        Assert.Equal([SpeakerIds.Me, "speaker-1"], edited.Speakers.Select(p => p.Id));
    }

    [Fact]
    public void ReassigningToANewRemoteIdCreatesThatSpeaker()
    {
        var segments = new List<TranscriptSegment> { Sys(0, 2, "a"), Sys(3, 5, "b") }
            .Select(s => s with { SpeakerId = "speaker-1" }).ToList();
        var speakers = new List<SpeakerProfile> { new("speaker-1", Track.System, null, 1, null) };

        var edited = SpeakerEdits.Apply(segments, speakers, [new ReassignSegment(Track.System, S(3), "speaker-2")]);

        var created = edited.Speakers.Single(p => p.Id == "speaker-2");
        Assert.Equal(Track.System, created.Track);
        Assert.Equal(2, created.ColorSlot);
    }
}
