using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Speakers;
using Quill.Core.Storage;
using Xunit;

namespace Quill.Core.Tests.Speakers;

public sealed class SpeakerResolutionServiceTests : IDisposable
{
    private const string SessionId = "2026-11-20_09-00-00";

    private const string Meta = """
        {
          "started": "2026-11-20T09:00:00+01:00",
          "ended": "2026-11-20T09:30:00+01:00",
          "duration_seconds": 1800,
          "files": { "mic": "mic.wav", "system": "system.wav" },
          "start_offset_ms": { "mic": 0, "system": 250 }
        }
        """;

    // v1 output of the current pipeline: mic = "me", system = "them". The second "me" is echo.
    private const string Transcript = """
        {
          "engine": "whisper.net",
          "model": "ggml-large-v3-turbo-q5_0.bin",
          "created_at": "2026-11-20T09:31:00+01:00",
          "segments": [
            { "speaker": "me", "start_ms": 0, "end_ms": 3000, "text": "Good morning everyone, shall we start" },
            { "speaker": "them", "start_ms": 4000, "end_ms": 9000, "text": "Yes, the budget is due on Monday" },
            { "speaker": "me", "start_ms": 4100, "end_ms": 9100, "text": "yes the budget is due on Monday" },
            { "speaker": "them", "start_ms": 10000, "end_ms": 14000, "text": "And I have a question about hiring" },
            { "speaker": "them", "start_ms": 15000, "end_ms": 18000, "text": "Back to the budget then" }
          ]
        }
        """;

    private readonly TempDirectory _temp = new();
    private readonly string _folder;
    private readonly FileSessionStore _store;
    private readonly FakeDiarizer _diarizer = new();
    private readonly RecordingScheduler _scheduler = new();

    public SpeakerResolutionServiceTests()
    {
        _folder = _temp.Session(SessionId);
        _temp.Write(_folder, "meta.json", Meta);
        _temp.Write(_folder, "transcript.json", Transcript);
        _temp.Write(_folder, "mic.wav", "");
        _temp.Write(_folder, "system.wav", "");
        _store = new FileSessionStore(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private SpeakerResolutionService Service() =>
        new(_store, new FileSpeakerResolutionStore(_store), new CrossTrackEchoClassifier(), _diarizer, _scheduler);

    private async Task<IReadOnlyList<TranscriptSegment>> Final() =>
        await _store.LoadTranscriptAsync(SessionId, TranscriptPass.Final, CancellationToken.None);

    [Fact]
    public async Task ResolveRunsInBackgroundAndPersistsTranscriptAndSpeakers()
    {
        var outcome = await Service().ResolveAsync(SessionId, null, CancellationToken.None);

        Assert.Equal(SpeakerResolutionStatus.Resolved, outcome.Status);
        Assert.Equal([InferencePriority.Background], _scheduler.Priorities);
        Assert.Equal(Path.Combine(_folder, "system.wav"), _diarizer.Requests.Single().AudioPath);
        Assert.Equal(Track.System, _diarizer.Requests.Single().Track);
        Assert.Equal(TimeSpan.FromMilliseconds(250), _diarizer.Requests.Single().StartOffset);

        var segments = await Final();
        Assert.Equal([SpeakerIds.Me, "speaker-1", "speaker-2", "speaker-1"], segments.Select(s => s.SpeakerId));
        Assert.Equal(1, outcome.EchoSegmentsRemoved);

        var speakers = await _store.LoadSpeakersAsync(SessionId, CancellationToken.None);
        Assert.Equal([SpeakerIds.Me, "speaker-1", "speaker-2"], speakers.Select(s => s.Id));

        var profiles = await Service().LoadSpeakersAsync(SessionId, CancellationToken.None);
        Assert.Equal([0, 1, 2], profiles.Select(p => p.ColorSlot));
        Assert.Equal([1f, 0f], profiles.Single(p => p.Id == "speaker-1").Embedding!);

        var header = File.ReadAllText(Path.Combine(_folder, "transcript.json"));
        Assert.Contains("ggml-large-v3-turbo-q5_0.bin", header);
    }

    [Fact]
    public async Task ResolveTwiceGivesTheSameResult()
    {
        await Service().ResolveAsync(SessionId, null, CancellationToken.None);
        var first = await Final();
        await Service().ResolveAsync(SessionId, null, CancellationToken.None);

        Assert.Equal(first, await Final());
    }

    [Fact]
    public async Task MissingTranscriptChangesNothing()
    {
        File.Delete(Path.Combine(_folder, "transcript.json"));

        var outcome = await Service().ResolveAsync(SessionId, null, CancellationToken.None);

        Assert.Equal(SpeakerResolutionStatus.NoTranscript, outcome.Status);
        Assert.Empty(_diarizer.Requests);
        Assert.False(File.Exists(Path.Combine(_folder, "speakers.json")));
    }

    [Fact]
    public async Task RenameIsPersistedIdempotentAndSurvivesResolve()
    {
        var service = Service();
        await service.ResolveAsync(SessionId, null, CancellationToken.None);

        await service.RenameAsync(SessionId, "speaker-2", "Juan", CancellationToken.None);
        await service.RenameAsync(SessionId, "speaker-2", "Juan", CancellationToken.None);
        await service.ResolveAsync(SessionId, null, CancellationToken.None);

        var speakers = await _store.LoadSpeakersAsync(SessionId, CancellationToken.None);
        Assert.Equal("Juan", speakers.Single(s => s.Id == "speaker-2").DisplayName);
    }

    [Fact]
    public async Task RenameOfUnknownSpeakerThrows()
    {
        var service = Service();
        await service.ResolveAsync(SessionId, null, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RenameAsync(SessionId, "speaker-9", "Nobody", CancellationToken.None));
    }

    [Fact]
    public async Task MergeIsPersistedIdempotentAndSurvivesResolve()
    {
        var service = Service();
        await service.ResolveAsync(SessionId, null, CancellationToken.None);

        await service.MergeAsync(SessionId, "speaker-1", "speaker-2", CancellationToken.None);
        await service.MergeAsync(SessionId, "speaker-1", "speaker-2", CancellationToken.None);
        Assert.Equal([SpeakerIds.Me, "speaker-1", "speaker-1", "speaker-1"], (await Final()).Select(s => s.SpeakerId));

        await service.ResolveAsync(SessionId, null, CancellationToken.None);
        Assert.Equal([SpeakerIds.Me, "speaker-1", "speaker-1", "speaker-1"], (await Final()).Select(s => s.SpeakerId));
        var speakers = await _store.LoadSpeakersAsync(SessionId, CancellationToken.None);
        Assert.Equal([SpeakerIds.Me, "speaker-1"], speakers.Select(s => s.Id));
    }

    [Fact]
    public async Task ReassignIsPersistedIdempotentAndSurvivesResolve()
    {
        var service = Service();
        await service.ResolveAsync(SessionId, null, CancellationToken.None);

        await service.ReassignAsync(SessionId, 3, "speaker-2", CancellationToken.None);
        await service.ReassignAsync(SessionId, 3, "speaker-2", CancellationToken.None);
        Assert.Equal("speaker-2", (await Final())[3].SpeakerId);

        await service.ResolveAsync(SessionId, null, CancellationToken.None);
        Assert.Equal("speaker-2", (await Final())[3].SpeakerId);

        var state = await new FileSpeakerResolutionStore(_store).LoadAsync(SessionId, CancellationToken.None);
        Assert.Single(state.Edits);
    }

    [Fact]
    public async Task ReassignRejectsUnknownIdsAndBadIndexes()
    {
        var service = Service();
        await service.ResolveAsync(SessionId, null, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ReassignAsync(SessionId, 99, "speaker-1", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReassignAsync(SessionId, 0, "them", CancellationToken.None));
    }

    [Fact]
    public async Task LegacySessionListsMeAndThemWithoutResolving()
    {
        var profiles = await Service().LoadSpeakersAsync(SessionId, CancellationToken.None);

        Assert.Equal([SpeakerIds.Me, SpeakerIds.LegacyThem], profiles.Select(p => p.Id));
        Assert.Equal([Track.Microphone, Track.System], profiles.Select(p => p.Track));
    }

    private sealed class FakeDiarizer : IDiarizer
    {
        public List<DiarizationRequest> Requests { get; } = [];

        public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            IReadOnlyList<SpeakerTurn> turns =
            [
                new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9), 1),
                new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), 0),
                new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(18), 1),
            ];
            var embeddings = new Dictionary<int, float[]> { [0] = [0f, 1f], [1] = [1f, 0f] };
            return Task.FromResult(new DiarizationResult(request.Track, turns, embeddings));
        }
    }

    private sealed class RecordingScheduler : IInferenceScheduler
    {
        public List<InferencePriority> Priorities { get; } = [];

        public int PendingCount => 0;

        public Task<T> RunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default)
        {
            Priorities.Add(priority);
            return job(cancellationToken);
        }

        public async Task<(bool Ran, T? Result)> TryRunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default)
        {
            Priorities.Add(priority);
            return (true, await job(cancellationToken));
        }
    }
}
