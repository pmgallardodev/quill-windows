using Xunit;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Search;
using Quill.Core.Tests.Summaries;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Search;

public sealed class MeetingIndexerTests
{
    private readonly FakeSessionStore _store = new();
    private readonly FakeEmbeddingModel _model = new();
    private readonly MemoryEmbeddingIndex _index = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly SearchCatalog _catalog = new();

    public MeetingIndexerTests()
    {
        _store.Transcripts[("s1", TranscriptPass.Final)] = [Line(0, 5, "hablamos del presupuesto"), Line(6, 9, "y del calendario", "speaker-1")];
        _store.Transcripts[("s2", TranscriptPass.Final)] = [Line(0, 5, "revisión del contrato")];
        _store.Notes["s1"] = "- [00:00:03] nota sobre cifras\n";
    }

    private MeetingIndexer Indexer(IEmbeddingModel? model = null) =>
        new(_store, _catalog, _index, model ?? _model, _scheduler);

    [Fact]
    public async Task Indexes_transcripts_notes_and_summaries_with_vectors_in_the_background()
    {
        _store.Summaries[("s1", SummaryKind.Final)] = new MeetingSummary { Decisions = [new Decision("Aprobar", "me", S(1))] };

        var report = await Indexer().IndexAsync([SearchData.Session("s1", "Presupuesto"), SearchData.Session("s2")], CancellationToken.None);

        Assert.Equal(2, report.Indexed);
        Assert.True(report.VectorsAvailable);
        Assert.Equal("Presupuesto", _catalog.Get("s1")!.Title);
        Assert.Contains(_catalog.Get("s1")!.Passages, passage => passage.Kind == PassageKinds.Note);
        Assert.Contains(_catalog.Get("s1")!.Passages, passage => passage.Kind == PassageKinds.Summary);
        Assert.Equal(_catalog.AllPassages().Count, _index.Records.Count);
        Assert.All(_index.Records.Values, record => Assert.Equal(_model.Dimensions, record.Vector.Length));
        Assert.All(_scheduler.Ran, priority => Assert.Equal(InferencePriority.Background, priority));
        Assert.DoesNotContain(_model.Embedded, text => text.Contains("[00:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_sessions_are_skipped_and_changed_ones_are_reembedded()
    {
        var sessions = new[] { SearchData.Session("s1"), SearchData.Session("s2") };
        await Indexer().IndexAsync(sessions, CancellationToken.None);
        var embeddedBefore = _model.Embedded.Count;

        var again = await Indexer().IndexAsync(sessions, CancellationToken.None);
        Assert.Equal(0, again.Indexed);
        Assert.Equal(2, again.Unchanged);
        Assert.Equal(embeddedBefore, _model.Embedded.Count);

        _store.Transcripts[("s2", TranscriptPass.Final)] = [Line(0, 5, "revisión del contrato nuevo")];
        var changed = await Indexer().IndexAsync(sessions, CancellationToken.None);
        Assert.Equal(1, changed.Indexed);
        Assert.Contains(_index.Records.Values, record => record.SessionId == "s2" && record.Text.Contains("nuevo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_embedding_model_indexes_lexically_and_embeds_later_when_it_appears()
    {
        var missing = new FakeEmbeddingModel { Throw = new EmbeddingModelUnavailableException("no model") };

        var report = await Indexer(missing).IndexAsync([SearchData.Session("s1")], CancellationToken.None);

        Assert.Equal(1, report.Indexed);
        Assert.False(report.VectorsAvailable);
        Assert.NotEmpty(_catalog.Get("s1")!.Passages);
        Assert.Null(_catalog.Get("s1")!.EmbeddingModel);
        Assert.Empty(_index.Records);

        var later = await Indexer().IndexAsync([SearchData.Session("s1")], CancellationToken.None);
        Assert.Equal(1, later.Indexed);
        Assert.Equal(_model.ModelName, _catalog.Get("s1")!.EmbeddingModel);
        Assert.NotEmpty(_index.Records);
    }

    [Fact]
    public async Task IndexAll_removes_sessions_that_no_longer_exist_or_lost_their_transcript()
    {
        await Indexer().IndexAsync([SearchData.Session("s1"), SearchData.Session("s2")], CancellationToken.None);
        _store.Transcripts.Remove(("s2", TranscriptPass.Final));

        var report = await Indexer().IndexAsync([SearchData.Session("s2")], CancellationToken.None, removeMissing: true);

        Assert.Null(_catalog.Get("s1"));
        Assert.Null(_catalog.Get("s2"));
        Assert.Equal(2, report.Removed);
        Assert.Empty(_index.Records);
    }

    [Fact]
    public async Task Catalog_round_trips_through_its_file()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "catalog.json");
        var catalog = new SearchCatalog(path);
        await new MeetingIndexer(_store, catalog, _index, _model, _scheduler).IndexAsync([SearchData.Session("s1", "Título")], CancellationToken.None);

        var loaded = await SearchCatalog.LoadAsync(path, CancellationToken.None);

        var entry = loaded.Get("s1")!;
        Assert.Equal("Título", entry.Title);
        Assert.Equal(catalog.Get("s1")!.ContentHash, entry.ContentHash);
        Assert.Equal(catalog.Get("s1")!.Passages, entry.Passages, PassageComparer.Instance);
    }

    [Fact]
    public async Task A_corrupt_catalog_file_loads_empty()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "catalog.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var loaded = await SearchCatalog.LoadAsync(path, CancellationToken.None);

        Assert.Empty(loaded.Sessions);
    }

    private sealed class PassageComparer : IEqualityComparer<Passage>
    {
        public static PassageComparer Instance { get; } = new();

        public bool Equals(Passage? x, Passage? y) =>
            x is not null && y is not null && x.Id == y.Id && x.Text == y.Text && x.Start == y.Start && x.End == y.End &&
            x.Kind == y.Kind && x.Speakers.SequenceEqual(y.Speakers);

        public int GetHashCode(Passage obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
