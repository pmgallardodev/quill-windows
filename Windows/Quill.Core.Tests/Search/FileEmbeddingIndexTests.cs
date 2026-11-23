using Xunit;
using Quill.Core.Ports;
using Quill.Core.Search;

namespace Quill.Core.Tests.Search;

public sealed class FileEmbeddingIndexTests
{
    private static EmbeddingRecord Record(string id, string session, params float[] vector) =>
        new(id, session, "transcript", $"text {id}", TimeSpan.FromSeconds(id.Length), VectorMath.Normalize(vector));

    [Fact]
    public async Task Upsert_search_and_reload_from_disk()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, ".quill-index");
        var index = new FileEmbeddingIndex(directory);

        await index.UpsertAsync(
        [
            Record("a", "s1", 1, 0, 0),
            Record("b", "s1", 0.9f, 0.1f, 0),
            Record("c", "s2", 0, 1, 0),
        ], CancellationToken.None);

        var hits = await index.SearchAsync([1, 0, 0], 2, null, CancellationToken.None);
        Assert.Equal(["a", "b"], hits.Select(hit => hit.Record.Id));
        Assert.Equal(1d, hits[0].Score, 5);

        Assert.True(File.Exists(Path.Combine(directory, FileEmbeddingIndex.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(directory, FileEmbeddingIndex.VectorsFileName)));
        Assert.Equal(3 * 3 * sizeof(float), new FileInfo(Path.Combine(directory, FileEmbeddingIndex.VectorsFileName)).Length);

        var reloaded = new FileEmbeddingIndex(directory);
        var again = await reloaded.SearchAsync([0, 1, 0], 1, null, CancellationToken.None);
        var record = Assert.Single(again).Record;
        Assert.Equal("c", record.Id);
        Assert.Equal("s2", record.SessionId);
        Assert.Equal("text c", record.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), record.At);
    }

    [Fact]
    public async Task Upsert_replaces_by_id_and_filters_apply()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("a", "s1", 1, 0), Record("b", "s2", 1, 0)], CancellationToken.None);
        await index.UpsertAsync([Record("a", "s1", 0, 1)], CancellationToken.None);

        var hits = await index.SearchAsync([0, 1], 5, new EmbeddingFilter(SessionId: "s1"), CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("a", hit.Record.Id);
        Assert.Equal(1d, hit.Score, 5);
        Assert.Empty(await index.SearchAsync([0, 1], 5, new EmbeddingFilter(Kind: "voice"), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_session_removes_only_its_records_and_persists()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("a", "s1", 1, 0), Record("b", "s2", 1, 0)], CancellationToken.None);

        await index.DeleteSessionAsync("s1", CancellationToken.None);

        var hits = await new FileEmbeddingIndex(temp.Path).SearchAsync([1, 0], 5, null, CancellationToken.None);
        Assert.Equal("b", Assert.Single(hits).Record.Id);
    }

    [Fact]
    public async Task A_new_dimension_resets_the_index_instead_of_mixing_models()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("a", "s1", 1, 0)], CancellationToken.None);

        await index.UpsertAsync([Record("b", "s2", 1, 0, 0)], CancellationToken.None);

        var hits = await index.SearchAsync([1, 0, 0], 5, null, CancellationToken.None);
        Assert.Equal("b", Assert.Single(hits).Record.Id);
        Assert.Empty(await index.SearchAsync([1, 0], 5, null, CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_or_truncated_files_load_as_an_empty_index()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("a", "s1", 1, 0)], CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, FileEmbeddingIndex.VectorsFileName), [1, 2, 3]);

        Assert.Empty(await new FileEmbeddingIndex(temp.Path).SearchAsync([1, 0], 5, null, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(temp.Path, FileEmbeddingIndex.ManifestFileName), "nope");
        Assert.Empty(await new FileEmbeddingIndex(temp.Path).SearchAsync([1, 0], 5, null, CancellationToken.None));
    }
}
