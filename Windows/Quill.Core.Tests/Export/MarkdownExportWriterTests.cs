using System.Text.Json;
using Quill.Core.Export;
using Xunit;

namespace Quill.Core.Tests.Export;

public sealed class MarkdownExportWriterTests
{
    private static string Read(string path) => File.ReadAllText(path);

    [Fact]
    public void CreatesTheFileAndThenReportsUnchanged()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);

        var first = writer.Write("s1", "2026-11-23-daily.md", "uno\n", overwriteEdited: false);
        var second = writer.Write("s1", "2026-11-23-daily.md", "uno\n", overwriteEdited: false);

        Assert.Equal(MarkdownExportStatus.Created, first.Status);
        Assert.Equal(Path.Combine(temp.Path, "2026-11-23-daily.md"), first.Path);
        Assert.Equal("uno\n", Read(first.Path!));
        Assert.Equal(MarkdownExportStatus.Unchanged, second.Status);
        Assert.True(File.Exists(Path.Combine(temp.Path, MarkdownExportWriter.IndexRelativePath)));
    }

    [Fact]
    public void UpdatesAFileTheUserDidNotTouch()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);
        writer.Write("s1", "a.md", "uno\n", false);

        var result = writer.Write("s1", "a.md", "dos\n", false);

        Assert.Equal(MarkdownExportStatus.Updated, result.Status);
        Assert.Equal("dos\n", Read(result.Path!));
    }

    [Fact]
    public void NeverOverwritesAFileEditedByTheUserUnlessAllowed()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);
        var path = writer.Write("s1", "a.md", "uno\n", false).Path!;
        File.WriteAllText(path, "uno\nmis notas\n");

        var skipped = writer.Write("s1", "a.md", "dos\n", overwriteEdited: false);
        Assert.Equal(MarkdownExportStatus.SkippedEdited, skipped.Status);
        Assert.Equal("uno\nmis notas\n", Read(path));

        var forced = new MarkdownExportWriter(temp.Path).Write("s1", "a.md", "dos\n", overwriteEdited: true);
        Assert.Equal(MarkdownExportStatus.Updated, forced.Status);
        Assert.Equal("dos\n", Read(path));
    }

    [Fact]
    public void KeepsTheFirstFileNameOfASessionWhenTheTitleChanges()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);
        var first = writer.Write("s1", "a.md", "uno\n", false);

        var renamed = writer.Write("s1", "b.md", "dos\n", false);

        Assert.Equal(first.Path, renamed.Path);
        Assert.False(File.Exists(Path.Combine(temp.Path, "b.md")));
    }

    [Fact]
    public void NeverTakesOverAFileOfAnotherSessionOrAnUnknownFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.md"), "mío\n");
        var writer = new MarkdownExportWriter(temp.Path);

        var s1 = writer.Write("s1", "a.md", "s1\n", false);
        var s2 = writer.Write("s2", "a.md", "s2\n", false);

        Assert.Equal(Path.Combine(temp.Path, "a-2.md"), s1.Path);
        Assert.Equal(Path.Combine(temp.Path, "a-3.md"), s2.Path);
        Assert.Equal("mío\n", Read(Path.Combine(temp.Path, "a.md")));
    }

    [Fact]
    public void RecreatesAnExportTheUserDeleted()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);
        var path = writer.Write("s1", "a.md", "uno\n", false).Path!;
        File.Delete(path);

        var result = writer.Write("s1", "a.md", "uno\n", false);

        Assert.Equal(MarkdownExportStatus.Created, result.Status);
        Assert.Equal(path, result.Path);
    }

    [Fact]
    public void ACorruptIndexIsTreatedAsUnknownFilesAndNeverOverwrites()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);
        var path = writer.Write("s1", "a.md", "uno\n", false).Path!;
        File.WriteAllText(Path.Combine(temp.Path, MarkdownExportWriter.IndexRelativePath), "{ broken");

        var result = writer.Write("s1", "a.md", "dos\n", false);

        Assert.Equal(Path.Combine(temp.Path, "a-2.md"), result.Path);
        Assert.Equal("uno\n", Read(path));
    }

    [Fact]
    public async Task ParallelWritersKeepIndependentExportsAndMappingsStable()
    {
        const int rounds = 5;
        const int writersPerRound = 12;

        for (var round = 0; round < rounds; round++)
        {
            using var temp = new TempDirectory();
            using var ready = new CountdownEvent(writersPerRound);
            using var start = new ManualResetEventSlim(false);
            var writers = Enumerable.Range(0, writersPerRound)
                .Select(index => (SessionId: $"round-{round}-session-{index}", Content: $"content-{round}-{index}\n"))
                .Select(item => Task.Factory.StartNew(() =>
                {
                    var writer = new MarkdownExportWriter(temp.Path);
                    ready.Signal();
                    start.Wait();
                    return (item.SessionId, item.Content, Result: writer.Write(item.SessionId, "shared-title.md", item.Content, false));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Writers did not reach the synchronized start.");
            start.Set();
            var results = await Task.WhenAll(writers);

            Assert.Equal(writersPerRound, results.Select(item => item.Result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var item in results)
            {
                Assert.Equal(MarkdownExportStatus.Created, item.Result.Status);
                Assert.Equal(item.Content, Read(item.Result.Path!));
            }

            using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, MarkdownExportWriter.IndexRelativePath)));
            Assert.Equal(writersPerRound, index.RootElement.GetProperty("files").EnumerateObject().Count());

            foreach (var item in results)
            {
                var repeated = new MarkdownExportWriter(temp.Path).Write(item.SessionId, "shared-title.md", item.Content, false);
                Assert.Equal(MarkdownExportStatus.Unchanged, repeated.Status);
                Assert.Equal(item.Result.Path, repeated.Path);
            }
        }
    }

    [Fact]
    public void RejectsFileNamesThatLeaveTheFolder()
    {
        using var temp = new TempDirectory();
        var writer = new MarkdownExportWriter(temp.Path);

        Assert.Throws<ArgumentException>(() => writer.Write("s1", "../x.md", "x", false));
        Assert.Throws<ArgumentException>(() => writer.Write("s1", "x.txt", "x", false));
    }
}
