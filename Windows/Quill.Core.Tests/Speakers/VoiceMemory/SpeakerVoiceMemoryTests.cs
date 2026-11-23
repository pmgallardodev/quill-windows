using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Speakers;
using Quill.Core.Speakers.VoiceMemory;
using Quill.Core.Storage;
using Xunit;

namespace Quill.Core.Tests.Speakers.VoiceMemory;

/// <summary>Speaker resolution with voice memory (F11) and in-person mode (F13).</summary>
public sealed class SpeakerVoiceMemoryTests : IDisposable
{
    private const string First = "2026-11-20_09-00-00";
    private const string Second = "2026-11-21_09-00-00";

    private const string Meta = """
        {
          "started": "2026-11-20T09:00:00+01:00",
          "ended": "2026-11-20T09:30:00+01:00",
          "duration_seconds": 1800,
          "files": { "mic": "mic.wav", "system": "system.wav" },
          "start_offset_ms": { "mic": 0, "system": 0 }
        }
        """;

    // 30 s of genuine mic speech (enough to learn the owner), then two remote voices.
    private const string Transcript = """
        {
          "engine": "whisper.net",
          "model": "m",
          "segments": [
            { "speaker": "me", "start_ms": 0, "end_ms": 10000, "text": "Let me walk you through the plan" },
            { "speaker": "me", "start_ms": 10000, "end_ms": 30000, "text": "for this quarter and the next one" },
            { "speaker": "them", "start_ms": 31000, "end_ms": 36000, "text": "Sounds good to me" },
            { "speaker": "them", "start_ms": 37000, "end_ms": 42000, "text": "I have a question about hiring" }
          ]
        }
        """;

    private static readonly float[] JuanVoice = [1f, 0f, 0f];
    private static readonly float[] AnaVoice = [0f, 1f, 0f];
    private static readonly float[] OwnerVoice = [0f, 0f, 1f];

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;
    private readonly TrackDiarizer _diarizer = new();
    private readonly VoiceMemoryService _voices;

    public SpeakerVoiceMemoryTests()
    {
        foreach (var session in new[] { First, Second })
        {
            var folder = _temp.Session(session);
            _temp.Write(folder, "meta.json", Meta);
            _temp.Write(folder, "transcript.json", Transcript);
            _temp.Write(folder, "mic.wav", "");
            _temp.Write(folder, "system.wav", "");
        }

        _store = new FileSessionStore(_temp.Path);
        _voices = new VoiceMemoryService(new FileVoiceLibraryStore(_temp.Path));
    }

    public void Dispose() => _temp.Dispose();

    private SpeakerResolutionService Service() =>
        new(_store, new FileSpeakerResolutionStore(_store), new CrossTrackEchoClassifier(), _diarizer, new ImmediateScheduler(), voices: _voices);

    private Task<SpeakerResolutionState> State(string session) =>
        new FileSpeakerResolutionStore(_store).LoadAsync(session, CancellationToken.None);

    private async Task<string?> NameOf(string session, string speakerId) =>
        (await _store.LoadSpeakersAsync(session, CancellationToken.None)).Single(s => s.Id == speakerId).DisplayName;

    private async Task NameJuanInFirstMeetingAsync(SpeakerResolutionService service)
    {
        await service.ResolveAsync(First, null, CancellationToken.None);
        await service.RenameAsync(First, "speaker-1", "Juan", CancellationToken.None);
    }

    [Fact]
    public async Task NamingASpeakerLetsTheNextMeetingSuggestTheNameWithoutApplyingIt()
    {
        var service = Service();
        await NameJuanInFirstMeetingAsync(service);

        await service.ResolveAsync(Second, null, CancellationToken.None);

        var suggestion = Assert.Single((await State(Second)).Suggestions);
        Assert.Equal(("speaker-1", "Juan"), (suggestion.SpeakerId, suggestion.Name));
        Assert.Null(await NameOf(Second, "speaker-1"));
    }

    [Fact]
    public async Task ConfirmingASuggestionRenamesAndClearsIt()
    {
        var service = Service();
        await NameJuanInFirstMeetingAsync(service);
        await service.ResolveAsync(Second, null, CancellationToken.None);

        await service.ConfirmSuggestionAsync(Second, "speaker-1", CancellationToken.None);

        Assert.Equal("Juan", await NameOf(Second, "speaker-1"));
        Assert.Empty((await State(Second)).Suggestions);
        Assert.Equal(2, (await _voices.LoadAsync(CancellationToken.None)).Named.Single().Count);
    }

    [Fact]
    public async Task RejectingASuggestionIsRememberedForThatMeeting()
    {
        var service = Service();
        await NameJuanInFirstMeetingAsync(service);
        await service.ResolveAsync(Second, null, CancellationToken.None);

        await service.RejectSuggestionAsync(Second, "speaker-1", CancellationToken.None);
        await service.ResolveAsync(Second, null, CancellationToken.None);

        var state = await State(Second);
        Assert.Empty(state.Suggestions);
        Assert.Single(state.Rejections);
        Assert.Null(await NameOf(Second, "speaker-1"));
    }

    [Fact]
    public async Task AutoApplyNamesConfidentMatches()
    {
        var service = Service();
        await NameJuanInFirstMeetingAsync(service);
        await _voices.SetAutoApplyAsync(true, CancellationToken.None);

        await service.ResolveAsync(Second, null, CancellationToken.None);

        Assert.Equal("Juan", await NameOf(Second, "speaker-1"));
        Assert.Empty((await State(Second)).Suggestions);
    }

    [Fact]
    public async Task OnlineMeetingsTeachTheOwnersVoiceFromTheMicrophone()
    {
        await Service().ResolveAsync(First, null, CancellationToken.None);

        Assert.Contains(_diarizer.Requests, request => request.Track == Track.Microphone);
        Assert.Equal(OwnerVoice, (await _voices.LoadAsync(CancellationToken.None)).Owner!.Embedding);
        // Learning never changes who is "me" in an online meeting.
        Assert.Equal(SpeakerIds.Me, (await _store.LoadTranscriptAsync(First, TranscriptPass.Final, CancellationToken.None))[0].SpeakerId);
        Assert.False((await State(First)).InPerson);
    }

    [Fact]
    public async Task WithoutVoiceMemoryTheMicrophoneIsNotDiarized()
    {
        var service = new SpeakerResolutionService(
            _store, new FileSpeakerResolutionStore(_store), new CrossTrackEchoClassifier(), _diarizer, new ImmediateScheduler());

        await service.ResolveAsync(First, null, CancellationToken.None);

        Assert.All(_diarizer.Requests, request => Assert.Equal(Track.System, request.Track));
    }

    [Fact]
    public async Task InPersonModeDiarizesTheMicAndGivesOtherMicVoicesTheirOwnSpeaker()
    {
        var service = Service();
        await service.ResolveAsync(First, null, CancellationToken.None); // learns the owner
        // In person: the first 10 s are someone else sitting next to the owner.
        _diarizer.Microphone = new DiarizationResult(
            Track.Microphone,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), 4), new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), 2)],
            new Dictionary<int, float[]> { [4] = AnaVoice, [2] = OwnerVoice });

        await service.SetInPersonAsync(Second, true, CancellationToken.None);
        await service.ResolveAsync(Second, null, CancellationToken.None);

        Assert.True((await State(Second)).InPerson);
        var speakers = await _store.LoadSpeakersAsync(Second, CancellationToken.None);
        var micSpeakers = speakers.Where(s => s.Track == Track.Microphone).Select(s => s.Id).ToList();
        Assert.Contains(SpeakerIds.Me, micSpeakers);
        Assert.Equal(2, micSpeakers.Count);
        var me = (await service.LoadSpeakersAsync(Second, CancellationToken.None)).Single(p => p.Id == SpeakerIds.Me);
        Assert.Equal(OwnerVoice, me.Embedding);
    }

    /// <summary>Mic: the owner the whole time. System: Juan then Ana.</summary>
    private sealed class TrackDiarizer : IDiarizer
    {
        public List<DiarizationRequest> Requests { get; } = [];

        public DiarizationResult Microphone { get; set; } = new(
            Track.Microphone,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(30), 0)],
            new Dictionary<int, float[]> { [0] = OwnerVoice });

        public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(request.Track == Track.Microphone
                ? Microphone
                : new DiarizationResult(
                    Track.System,
                    [new(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(36), 0), new(TimeSpan.FromSeconds(37), TimeSpan.FromSeconds(42), 1)],
                    new Dictionary<int, float[]> { [0] = JuanVoice, [1] = AnaVoice }));
        }
    }

    private sealed class ImmediateScheduler : IInferenceScheduler
    {
        public int PendingCount => 0;

        public Task<T> RunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default) =>
            job(cancellationToken);

        public async Task<(bool Ran, T? Result)> TryRunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default) =>
            (true, await job(cancellationToken));
    }
}
