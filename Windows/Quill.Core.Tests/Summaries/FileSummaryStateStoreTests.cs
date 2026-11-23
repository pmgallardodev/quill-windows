using Quill.Core.Domain;
using Quill.Core.Storage;
using Quill.Core.Summaries;
using Xunit;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Summaries;

public sealed class FileSummaryStateStoreTests : IDisposable
{
    private const string SessionId = "2026-11-23_10-00-00";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileSummaryStateStore _state;
    private readonly string _folder;

    public FileSummaryStateStoreTests()
    {
        _folder = _temp.Session(SessionId);
        _sessions = new FileSessionStore(_temp.Path, new FixedClock(Now));
        _state = new FileSummaryStateStore(_sessions, new FixedClock(Now));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task MissingStateIsCurrent()
    {
        var state = await _state.LoadStateAsync(SessionId, CancellationToken.None);

        Assert.False(state.Outdated);
        Assert.False(File.Exists(Path.Combine(_folder, FileSummaryStateStore.StateFile)));
    }

    [Fact]
    public async Task OutdatedRoundTripsThroughTheSidecarAndClearingRemovesIt()
    {
        await _state.MarkOutdatedAsync(SessionId, "transcription retried", CancellationToken.None);

        var state = await _state.LoadStateAsync(SessionId, CancellationToken.None);
        Assert.True(state.Outdated);
        Assert.Equal("transcription retried", state.Reason);
        Assert.Equal(Now, state.Since);

        await _state.ClearOutdatedAsync(SessionId, CancellationToken.None);

        Assert.False((await _state.LoadStateAsync(SessionId, CancellationToken.None)).Outdated);
        Assert.False(File.Exists(Path.Combine(_folder, FileSummaryStateStore.StateFile)));
    }

    [Fact]
    public async Task CorruptSidecarReadsAsCurrent()
    {
        _temp.Write(_folder, FileSummaryStateStore.StateFile, "{ not json");

        Assert.False((await _state.LoadStateAsync(SessionId, CancellationToken.None)).Outdated);
    }

    [Fact]
    public async Task HasFinalSummaryFollowsSummaryJson()
    {
        Assert.False(await _state.HasFinalSummaryAsync(SessionId, CancellationToken.None));

        await _sessions.SaveSummaryAsync(SessionId, new MeetingSummary { ExecutiveSummary = "ok" }, CancellationToken.None);

        Assert.True(await _state.HasFinalSummaryAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task BackupKeepsOneGenerationAsSummaryPrevJson()
    {
        var first = new MeetingSummary { ExecutiveSummary = "uno", ActionItems = [new ActionItem("A", null, null, false, S(1), true)] };
        var second = new MeetingSummary { ExecutiveSummary = "dos" };

        await _state.BackupAsync(SessionId, first, CancellationToken.None);
        await _state.BackupAsync(SessionId, second, CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(_folder, FileSummaryStateStore.PreviousFile));
        Assert.Equal("dos", MeetingSummaryJson.Deserialize(text).ExecutiveSummary);
    }
}
