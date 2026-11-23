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
        Assert.False(File.Exists(Path.Combine(directory, $"{FileEmbeddingIndex.ManifestFileName}.tmp")));
        Assert.False(File.Exists(Path.Combine(directory, $"{FileEmbeddingIndex.VectorsFileName}.tmp")));
        Assert.False(File.Exists(Path.Combine(directory, "index.bin")));

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
    public async Task Failed_upsert_keeps_the_last_committed_warm_and_cold_view_and_can_retry()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("a", "s1", 1, 0)], CancellationToken.None);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                index.ReplaceSessionAsync("s1", [Record("a", "s1", 0, 1)], canceled.Token));
        }

        var writeObstacle = Path.Combine(temp.Path, $"{FileEmbeddingIndex.VectorsFileName}.tmp");
        Directory.CreateDirectory(writeObstacle);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            index.ReplaceSessionAsync("s1", [Record("a", "s1", 0, 1)], CancellationToken.None));
        Assert.True(exception is IOException or UnauthorizedAccessException);

        Assert.Equal("a", Assert.Single(await index.SearchAsync([1, 0], 5, null, CancellationToken.None)).Record.Id);
        Assert.Equal(1d, (await index.SearchAsync([1, 0], 5, null, CancellationToken.None))[0].Score, 5);
        Assert.Equal(1d, (await new FileEmbeddingIndex(temp.Path).SearchAsync([1, 0], 5, null, CancellationToken.None))[0].Score, 5);

        Directory.Delete(writeObstacle);
        await index.ReplaceSessionAsync("s1", [Record("a", "s1", 0, 1)], CancellationToken.None);
        Assert.Equal(1d, (await new FileEmbeddingIndex(temp.Path).SearchAsync([0, 1], 5, null, CancellationToken.None))[0].Score, 5);
    }

    [Fact]
    public async Task Failed_second_rename_restores_old_manifest_and_preserves_vectors()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("a", "s1", 1, 0)], CancellationToken.None);
        var manifestPath = Path.Combine(temp.Path, FileEmbeddingIndex.ManifestFileName);
        var vectorsPath = Path.Combine(temp.Path, FileEmbeddingIndex.VectorsFileName);
        var oldManifest = await File.ReadAllBytesAsync(manifestPath);
        var oldVectors = await File.ReadAllBytesAsync(vectorsPath);
        File.Delete(vectorsPath);
        Directory.CreateDirectory(vectorsPath);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            index.ReplaceSessionAsync("s1", [Record("a", "s1", 0, 1)], CancellationToken.None));

        Assert.Equal(oldManifest, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(1d, (await index.SearchAsync([1, 0], 5, null, CancellationToken.None))[0].Score, 5);
        Directory.Delete(vectorsPath);
        await File.WriteAllBytesAsync(vectorsPath, oldVectors);
        Assert.Equal(1d, (await new FileEmbeddingIndex(temp.Path).SearchAsync([1, 0], 5, null, CancellationToken.None))[0].Score, 5);
    }

    [Fact]
    public async Task Replace_session_removes_stale_records_and_preserves_other_sessions()
    {
        using var temp = new TempDirectory();
        var index = new FileEmbeddingIndex(temp.Path);
        await index.UpsertAsync([Record("old-1", "s1", 1, 0), Record("old-2", "s1", 1, 0), Record("other", "s2", 1, 0)], CancellationToken.None);

        await index.ReplaceSessionAsync("s1", [Record("new", "s1", 0, 1)], CancellationToken.None);

        var hits = await new FileEmbeddingIndex(temp.Path).SearchAsync([1, 0], 5, new EmbeddingFilter(SessionId: "s2"), CancellationToken.None);
        Assert.Equal("other", Assert.Single(hits).Record.Id);
        Assert.Equal("new", Assert.Single(await index.SearchAsync([0, 1], 5, new EmbeddingFilter(SessionId: "s1"), CancellationToken.None)).Record.Id);
    }

    [Fact]
    public async Task Legacy_pair_is_read_and_new_manifest_commits_digest()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, FileEmbeddingIndex.ManifestFileName),
            "{\"version\":1,\"dimensions\":2,\"records\":[{\"id\":\"old\",\"sessionId\":\"s1\",\"kind\":\"transcript\",\"text\":\"legacy\"}]}");
        var vector = new byte[2 * sizeof(float)];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(vector, 1f);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, FileEmbeddingIndex.VectorsFileName), vector);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "index.bin"), [1, 2, 3]);
        var index = new FileEmbeddingIndex(temp.Path);
        Assert.Equal("old", Assert.Single(await index.SearchAsync([1, 0], 5, null, CancellationToken.None)).Record.Id);

        await index.UpsertAsync([Record("new", "s2", 0, 1)], CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(temp.Path, FileEmbeddingIndex.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(temp.Path, FileEmbeddingIndex.VectorsFileName)));
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, FileEmbeddingIndex.ManifestFileName)));
        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("vectorsSha256").GetString()));
        Assert.False(File.Exists(Path.Combine(temp.Path, "index.bin")));

        // Simulate a downgrade followed by an older writer updating the authoritative pair.
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "index.bin"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, FileEmbeddingIndex.ManifestFileName),
            "{\"version\":1,\"dimensions\":2,\"records\":[{\"id\":\"legacy-update\",\"sessionId\":\"s3\",\"kind\":\"transcript\",\"text\":\"legacy update\"}]}");
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(vector, 1f);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, FileEmbeddingIndex.VectorsFileName), vector);
        var reloaded = new FileEmbeddingIndex(temp.Path);
        Assert.Equal("legacy-update", Assert.Single(await reloaded.SearchAsync([1, 0], 5, null, CancellationToken.None)).Record.Id);
        Assert.Empty(await reloaded.SearchAsync([1, 0], 5, new EmbeddingFilter(SessionId: "s1"), CancellationToken.None));
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
    }
}
