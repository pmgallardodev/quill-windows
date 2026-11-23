using Quill.Core.Domain;
using Quill.Core.Export;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Xunit;

namespace Quill.Core.Tests.Export;

public sealed class MeetingExportServiceTests
{
    [Fact]
    public async Task ExportsTheFinalSummaryAndFallsBackToTheLiveTranscript()
    {
        using var temp = new TempDirectory();
        var store = new ExportStore();
        store.Add(MarkdownMeetingExporterTests.Meeting("Daily"));
        store.Summaries[SummaryKind.Final] = MarkdownMeetingExporterTests.FullSummary();
        store.Live = [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Solo en vivo.", SpeakerIds.Me, Track.Microphone, Editable: false)];
        var service = new MeetingExportService(store);

        var result = await service.ExportAsync("2026-11-23_10-00-00", new ExportSettings { Folder = temp.Path }, CancellationToken.None);

        Assert.Equal(MarkdownExportStatus.Created, result.Status);
        Assert.Equal(Path.Combine(temp.Path, "2026-11-23-daily.md"), result.Path);
        var text = File.ReadAllText(result.Path!);
        Assert.Contains("template: \"client-call\"", text);
        Assert.Contains("**[00:00] Yo:** Solo en vivo.", text);
    }

    [Fact]
    public async Task AutoExportOnlyRunsWhenEnabledAndConfigured()
    {
        using var temp = new TempDirectory();
        var store = new ExportStore();
        store.Add(MarkdownMeetingExporterTests.Meeting());
        var service = new MeetingExportService(store);

        Assert.Null(await service.ExportIfEnabledAsync("2026-11-23_10-00-00", new ExportSettings { Folder = temp.Path }, CancellationToken.None));
        Assert.Null(await service.ExportIfEnabledAsync("2026-11-23_10-00-00", new ExportSettings { AutoExport = true }, CancellationToken.None));
        Assert.NotNull(await service.ExportIfEnabledAsync(
            "2026-11-23_10-00-00", new ExportSettings { Folder = temp.Path, AutoExport = true }, CancellationToken.None));
    }

    [Fact]
    public async Task ExportRequiresAFolderAndAnExistingSession()
    {
        var service = new MeetingExportService(new ExportStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync("x", new ExportSettings(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync("x", new ExportSettings { Folder = "/tmp/quill-nowhere" }, CancellationToken.None));
    }

    [Fact]
    public async Task ExportAllReportsEverySession()
    {
        using var temp = new TempDirectory();
        var store = new ExportStore();
        store.Add(MarkdownMeetingExporterTests.Meeting("Uno") with { Id = "a" });
        store.Add(MarkdownMeetingExporterTests.Meeting("Dos") with { Id = "b" });
        store.Broken.Add("b");
        var service = new MeetingExportService(store);

        var results = await service.ExportAllAsync(new ExportSettings { Folder = temp.Path }, null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(MarkdownExportStatus.Created, results.Single(r => r.SessionId == "a").Status);
        var failed = results.Single(r => r.SessionId == "b");
        Assert.Equal(MarkdownExportStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
    }

    private sealed class ExportStore : ISessionStore
    {
        private readonly List<Session> _sessions = [];

        public Dictionary<SummaryKind, MeetingSummary> Summaries { get; } = [];

        public IReadOnlyList<TranscriptSegment> Live { get; set; } = [];

        public HashSet<string> Broken { get; } = [];

        public void Add(Session session) => _sessions.Add(session);

        public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Session>>(_sessions);

        public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Broken.Contains(sessionId)
                ? throw new IOException("disk error")
                : Task.FromResult(_sessions.FirstOrDefault(s => s.Id == sessionId));

        public Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string sessionId, TranscriptPass pass, CancellationToken cancellationToken) =>
            Task.FromResult(pass == TranscriptPass.Live ? Live : []);

        public Task SaveTranscriptAsync(string sessionId, TranscriptPass pass, TranscriptInfo info, IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(Summaries.GetValueOrDefault(kind));

        public Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Speaker>>([]);

        public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
