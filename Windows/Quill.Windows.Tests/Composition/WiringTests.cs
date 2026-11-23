using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Composition;
using Quill.Windows.Tests.UI;
using Xunit;

namespace Quill.Windows.Tests.Composition;

public sealed class NotifyingSessionStoreTests
{
    private readonly FakeSessionStore _inner = new();
    private readonly List<string> _changed = [];

    private NotifyingSessionStore Store() => new(_inner, _changed.Add);

    [Fact]
    public async Task NotesSaveReportsTheSession()
    {
        await Store().SaveNotesAsync("s1", "# notes", CancellationToken.None);

        Assert.Equal(["s1"], _changed);
        Assert.Equal([("s1", "# notes")], _inner.SavedNotes);
    }

    [Fact]
    public async Task FinalSummarySaveReportsButLiveDoesNot()
    {
        var store = Store();

        await store.SaveSummaryAsync("s1", Samples.Summary(SummaryKind.Live), CancellationToken.None);
        await store.SaveSummaryAsync("s2", Samples.Summary(), CancellationToken.None);

        Assert.Equal(["s2"], _changed);
    }

    [Fact]
    public async Task FailedSaveReportsNothing()
    {
        _inner.FailSaves = true;

        await Assert.ThrowsAsync<IOException>(() => Store().SaveNotesAsync("s1", "x", CancellationToken.None));

        Assert.Empty(_changed);
    }

    [Fact]
    public async Task ReadsAreForwardedWithoutNotifying()
    {
        _inner.Notes["s1"] = "hola";

        Assert.Equal("hola", await Store().LoadNotesAsync("s1", CancellationToken.None));
        Assert.Empty(_changed);
    }
}

public sealed class SummaryOutdatedActionTests
{
    [Fact]
    public async Task ReadsTheOutdatedFlagThroughTheServices()
    {
        var services = new AppSessionActionsTests.Harness().Services() with
        {
            IsSummaryOutdated = (sessionId, _) => Task.FromResult(sessionId == "s1"),
        };
        var actions = new AppSessionActions(services);

        Assert.True(await actions.IsSummaryOutdatedAsync("s1", CancellationToken.None));
        Assert.False(await actions.IsSummaryOutdatedAsync("s2", CancellationToken.None));
    }

    [Fact]
    public async Task UnreadableStateIsNotOutdated()
    {
        var services = new AppSessionActionsTests.Harness().Services() with
        {
            IsSummaryOutdated = (_, _) => Task.FromException<bool>(new IOException("locked")),
        };

        Assert.False(await new AppSessionActions(services).IsSummaryOutdatedAsync("s1", CancellationToken.None));
    }
}
