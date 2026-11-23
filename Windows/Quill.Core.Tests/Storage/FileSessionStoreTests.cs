using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Quill.Core.Tests.Summaries;
using Quill.Windows.Sessions;
using Xunit;
using TranscriptSegment = Quill.Core.Domain.TranscriptSegment;

namespace Quill.Core.Tests.Storage;

public sealed class FileSessionStoreTests
{
    private const string V1Meta = """
        {
          "started": "2026-11-20T09:00:00+01:00",
          "ended": "2026-11-20T09:30:00+01:00",
          "duration_seconds": 1800,
          "files": { "mic": "mic.wav", "system": "system.wav" },
          "start_offset_ms": { "mic": 0, "system": 250 },
          "title": "Daily",
          "markers": [ 60000, 120500 ]
        }
        """;

    private const string V1Transcript = """
        {
          "engine": "whisper.net",
          "model": "ggml-large-v3-turbo-q5_0.bin",
          "created_at": "2026-11-20T09:31:00+01:00",
          "segments": [
            { "speaker": "me", "start_ms": 1000, "end_ms": 4000, "text": "Buenos dias" },
            { "speaker": "them", "start_ms": 4500, "end_ms": 9000, "text": "Hola" }
          ]
        }
        """;

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    private static FileSessionStore Store(TempDirectory temp) =>
        new(temp.Path, new FixedClock(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)));

    private static string V1Session(TempDirectory temp, string name = "2026-11-20_09-00-00")
    {
        var folder = temp.Session(name);
        temp.Write(folder, "meta.json", V1Meta);
        temp.Write(folder, "transcript.json", V1Transcript);
        return folder;
    }

    [Fact]
    public async Task V1FolderStillLoads()
    {
        using var temp = new TempDirectory();
        var folder = V1Session(temp);
        var store = Store(temp);

        var session = await store.LoadSessionAsync("2026-11-20_09-00-00", CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("Daily", session.Title);
        Assert.Equal(folder, session.Folder);
        Assert.Equal(TimeSpan.FromMinutes(30), session.Duration);
        Assert.Contains(new SessionTrack(Track.System, "system.wav", TimeSpan.FromMilliseconds(250)), session.Tracks);
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(120500)], session.Markers.Select(m => m.At));

        var transcript = await store.LoadTranscriptAsync(session.Id, TranscriptPass.Final, CancellationToken.None);
        Assert.Equal(
            [
                new TranscriptSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Buenos dias", "me", Track.Microphone),
                new TranscriptSegment(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9), "Hola", "them", Track.System),
            ],
            transcript);

        Assert.Null(await store.LoadSummaryAsync(session.Id, SummaryKind.Final, CancellationToken.None));
        Assert.Empty(await store.LoadSpeakersAsync(session.Id, CancellationToken.None));
        Assert.Equal(string.Empty, await store.LoadNotesAsync(session.Id, CancellationToken.None));
        Assert.Empty(await store.LoadTranscriptAsync(session.Id, TranscriptPass.Live, CancellationToken.None));
    }

    [Fact]
    public async Task V2ArtifactsRoundTripAndLegacyReadersStillWork()
    {
        using var temp = new TempDirectory();
        var folder = V1Session(temp);
        var store = Store(temp);
        var id = Path.GetFileName(folder);

        var summary = MeetingSummaryJsonTests.Sample();
        var live = summary with { Meta = summary.Meta with { Kind = SummaryKind.Live } };
        IReadOnlyList<Speaker> speakers = [new("me", Track.Microphone), new("speaker-1", Track.System, "Ana")];
        IReadOnlyList<TranscriptSegment> segments =
        [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Buenos días", "me", Track.Microphone),
            new(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9), "Hola", "speaker-1", Track.System, Edited: true),
        ];

        await store.SaveSummaryAsync(id, summary, CancellationToken.None);
        await store.SaveSummaryAsync(id, live, CancellationToken.None);
        await store.SaveSpeakersAsync(id, speakers, CancellationToken.None);
        await store.SaveNotesAsync(id, "- [00:01:00] revisar precios\n", CancellationToken.None);
        await store.SaveTranscriptAsync(id, TranscriptPass.Final, new TranscriptInfo("whisper.net", "turbo"), segments, CancellationToken.None);
        await store.SaveTranscriptAsync(id, TranscriptPass.Live, new TranscriptInfo("whisper.net", "small"), segments, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(folder, "summary.json")));
        Assert.True(File.Exists(Path.Combine(folder, "live", "summary.json")));
        Assert.True(File.Exists(Path.Combine(folder, "live", "transcript.json")));
        Assert.True(File.Exists(Path.Combine(folder, "speakers.json")));
        Assert.True(File.Exists(Path.Combine(folder, "notes.md")));
        Assert.True(File.Exists(Path.Combine(folder, "transcript.md")));

        Assert.Equal(summary.ActionItems, (await store.LoadSummaryAsync(id, SummaryKind.Final, CancellationToken.None))!.ActionItems);
        Assert.Equal(SummaryKind.Live, (await store.LoadSummaryAsync(id, SummaryKind.Live, CancellationToken.None))!.Meta.Kind);
        Assert.Equal(speakers, await store.LoadSpeakersAsync(id, CancellationToken.None));
        Assert.Equal("- [00:01:00] revisar precios\n", await store.LoadNotesAsync(id, CancellationToken.None));
        Assert.Equal(segments, await store.LoadTranscriptAsync(id, TranscriptPass.Final, CancellationToken.None));
        Assert.All(await store.LoadTranscriptAsync(id, TranscriptPass.Live, CancellationToken.None), s => Assert.False(s.Editable));

        // Old readers (v1 code paths still used by the Windows app) keep working on a v2 folder.
        Assert.True(TranscriptDocument.TryRead(folder, out var legacy));
        Assert.Equal("speaker-1", legacy!.Segments[1].Speaker);
        Assert.Equal(SessionState.Done, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
        Assert.NotNull(SessionMetadata.TryRead(folder));
        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "transcript.json")))!;
        Assert.Equal("system", written["segments"]![1]!["track"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListsSessionsNewestFirstAndSkipsFoldersWithoutMetadata()
    {
        using var temp = new TempDirectory();
        V1Session(temp, "2026-11-20_09-00-00");
        var newer = temp.Session("2026-11-21_09-00-00");
        temp.Write(newer, "meta.json", V1Meta.Replace("2026-11-20", "2026-11-21"));
        temp.Session("not-a-session");

        var sessions = await Store(temp).ListSessionsAsync(CancellationToken.None);

        Assert.Equal(["2026-11-21_09-00-00", "2026-11-20_09-00-00"], sessions.Select(s => s.Id));
    }

    [Fact]
    public async Task CorruptOptionalFilesReadAsEmpty()
    {
        using var temp = new TempDirectory();
        var folder = V1Session(temp);
        temp.Write(folder, "summary.json", "{ broken");
        temp.Write(folder, "speakers.json", "[]");
        var store = Store(temp);
        var id = Path.GetFileName(folder);

        Assert.Null(await store.LoadSummaryAsync(id, SummaryKind.Final, CancellationToken.None));
        Assert.Empty(await store.LoadSpeakersAsync(id, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public async Task RejectsUnsafeSessionIds(string id)
    {
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store(temp).LoadNotesAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task MissingSessionLoadsAsNull()
    {
        using var temp = new TempDirectory();

        Assert.Null(await Store(temp).LoadSessionAsync("nope", CancellationToken.None));
    }
}
