using Xunit;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Search;
using Quill.Core.Tests.Summaries;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Search;

public sealed class HybridSearchTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Friday = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeSessionStore _store = new();
    private readonly FakeEmbeddingModel _model = new();
    private readonly MemoryEmbeddingIndex _index = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly SearchCatalog _catalog = new();

    private async Task IndexAsync()
    {
        _store.Transcripts[("lunes", TranscriptPass.Final)] =
        [
            Line(0, 5, "Revisamos el presupuesto de marketing"),
            Line(200, 205, "El camión llega el jueves", "speaker-1"),
        ];
        _store.Transcripts[("viernes", TranscriptPass.Final)] = [Line(0, 5, "Hablamos del coste de la campaña")];
        await new MeetingIndexer(_store, _catalog, _index, _model, _scheduler).IndexAsync(
            [SearchData.Session("lunes", "Comité lunes", Monday), SearchData.Session("viernes", "Comité viernes", Friday)],
            CancellationToken.None);
    }

    [Fact]
    public async Task Lexical_matches_fold_accents_and_case()
    {
        await IndexAsync();
        var search = new HybridSearch(_catalog, _index, _model, _scheduler);

        var response = await search.SearchAsync(new SearchQuery("CAMION"), CancellationToken.None);

        var top = response.Results[0];
        Assert.Equal("lunes", top.Passage.SessionId);
        Assert.Contains("camión", top.Passage.Text, StringComparison.Ordinal);
        Assert.Equal("Comité lunes", top.SessionTitle);
        Assert.Equal(Monday, top.Started);
        Assert.NotNull(top.LexicalRank);
    }

    [Fact]
    public async Task Vector_side_finds_synonyms_the_lexical_side_misses_and_fuses_both()
    {
        _model.Synonyms["gasto"] = "coste";
        await IndexAsync();
        var search = new HybridSearch(_catalog, _index, _model, _scheduler);

        var response = await search.SearchAsync(new SearchQuery("gasto"), CancellationToken.None);

        Assert.True(response.UsedVectors);
        var top = response.Results[0];
        Assert.Equal("viernes", top.Passage.SessionId);
        Assert.Null(top.LexicalRank);
        Assert.Equal(1, top.VectorRank);
        Assert.Contains(InferencePriority.Interactive, _scheduler.Ran);
    }

    [Fact]
    public async Task Filters_restrict_by_session_and_date_range()
    {
        await IndexAsync();
        var search = new HybridSearch(_catalog, _index, _model, _scheduler);

        var bySession = await search.SearchAsync(new SearchQuery("presupuesto coste", SessionId: "viernes"), CancellationToken.None);
        Assert.All(bySession.Results, result => Assert.Equal("viernes", result.Passage.SessionId));
        Assert.NotEmpty(bySession.Results);

        var byDate = await search.SearchAsync(new SearchQuery("presupuesto coste", From: Friday.AddHours(-1)), CancellationToken.None);
        Assert.All(byDate.Results, result => Assert.Equal("viernes", result.Passage.SessionId));

        var before = await search.SearchAsync(new SearchQuery("presupuesto coste", To: Monday.AddHours(1)), CancellationToken.None);
        Assert.All(before.Results, result => Assert.Equal("lunes", result.Passage.SessionId));
    }

    [Fact]
    public async Task Missing_embedding_model_falls_back_to_lexical_only()
    {
        await IndexAsync();
        var missing = new FakeEmbeddingModel { Throw = new EmbeddingModelUnavailableException("no model") };
        var search = new HybridSearch(_catalog, _index, missing, _scheduler);

        var response = await search.SearchAsync(new SearchQuery("presupuesto"), CancellationToken.None);

        Assert.False(response.UsedVectors);
        Assert.Equal("lunes", response.Results[0].Passage.SessionId);

        var noModel = await new HybridSearch(_catalog, null, null).SearchAsync(new SearchQuery("presupuesto"), CancellationToken.None);
        Assert.False(noModel.UsedVectors);
        Assert.NotEmpty(noModel.Results);
    }

    [Fact]
    public async Task Blank_query_returns_nothing()
    {
        await IndexAsync();
        var response = await new HybridSearch(_catalog, _index, _model, _scheduler).SearchAsync(new SearchQuery("  "), CancellationToken.None);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Search_skips_catalog_entries_removed_while_embedding_is_pending()
    {
        await IndexAsync();
        var model = new DeferredEmbeddingModel(_model);
        var search = new HybridSearch(_catalog, _index, model, _scheduler);
        var pending = search.SearchAsync(new SearchQuery("presupuesto"), CancellationToken.None);
        await model.Started.Task;

        _catalog.Remove("lunes");
        model.Release.TrySetResult(true);

        var response = await pending;

        Assert.DoesNotContain(response.Results, result => result.Passage.SessionId == "lunes");
        Assert.All(response.Results, result => Assert.NotNull(_catalog.Get(result.Passage.SessionId)));
    }

    private sealed class DeferredEmbeddingModel(IEmbeddingModel inner) : IEmbeddingModel
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ModelName => inner.ModelName;
        public int Dimensions => inner.Dimensions;

        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.EmbedAsync(texts, cancellationToken);
        }
    }

    [Fact]
    public void Reciprocal_rank_fusion_rewards_items_in_both_lists()
    {
        var fused = HybridSearch.Fuse(["a", "b", "c"], ["c", "d"], k: 60);

        Assert.Equal("c", fused[0].Id);
        Assert.Equal(3, fused[0].LexicalRank);
        Assert.Equal(1, fused[0].VectorRank);
        Assert.Equal("a", fused[1].Id);
        Assert.Equal(["b", "d"], fused.Skip(2).Select(item => item.Id).Order());
    }
}
