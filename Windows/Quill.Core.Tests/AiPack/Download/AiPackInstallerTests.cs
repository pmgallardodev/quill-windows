using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Quill.Core.AiPack;
using Quill.Core.AiPack.Download;
using Quill.Core.Ports;
using Xunit;
using static Quill.Core.Tests.AiPack.Download.DownloadFixtures;

namespace Quill.Core.Tests.AiPack.Download;

public sealed class AiPackInstallerTests
{
    [Fact]
    public async Task DownloadsVerifiesAndRegistersEveryComponent()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            LlmUrl => FakeHttpHandler.Ok(LlmBytes),
            WhisperUrl => FakeHttpHandler.Ok(WhisperBytes),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var reports = new List<AiPackDownloadProgress>();

        await Installer(handler).InstallAsync(Plan(temp.Path, Llm(), Whisper()), new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(LlmBytes, File.ReadAllBytes(Path.Combine(temp.Path, "llm", "model.gguf")));
        Assert.Equal(WhisperBytes, File.ReadAllBytes(Path.Combine(temp.Path, "whisper", "ggml.bin")));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.partial", SearchOption.AllDirectories));

        var status = new ManifestAiPackLocator(temp.Path, temp.Path).Locate();
        Assert.True(status.IsInstalled);
        Assert.True(status.Has(AiPackComponent.LanguageModel));
        Assert.True(status.Has(AiPackComponent.LiveWhisper));

        var last = reports[^1];
        Assert.Equal(LlmBytes.Length + WhisperBytes.Length, last.TotalBytes);
        Assert.Equal(last.TotalBytes, last.TotalDone);
        Assert.Contains(reports, r => r.Component == AiPackComponent.LanguageModel && r.Phase == AiPackDownloadPhase.Completed);
        Assert.All(handler.Requests, r => Assert.Equal("Quill", r.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task KeepsExistingManifestEntriesAndKeys()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName), """
            { "version": "1.2.0", "extra": 7, "files": { "textEmbedding": "embeddings/e.gguf" } }
            """);

        await Installer(new FakeHttpHandler(_ => FakeHttpHandler.Ok(LlmBytes)))
            .InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName)))!;
        Assert.Equal("1.2.0", (string?)manifest["version"]);
        Assert.Equal(7, (int?)manifest["extra"]);
        Assert.Equal("embeddings/e.gguf", (string?)manifest["files"]!["textEmbedding"]);
        Assert.Equal("llm/model.gguf", (string?)manifest["files"]!["languageModel"]);
    }

    [Fact]
    public void RegisteringAWholeFileDropsItsSplitEntryButKeepsOthers()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName), """
            {
              "schema": 2, "version": "1.0.0",
              "files": { "languageModel": "llm/model.gguf" },
              "splitFiles": { "languageModel": { "parts": [] }, "textEmbedding": { "parts": [] } }
            }
            """);

        AiPackManifestWriter.Register(temp.Path, [Llm()]);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName)))!;
        Assert.Equal(2, (int?)manifest["schema"]);
        Assert.Null(manifest["splitFiles"]!["languageModel"]);
        Assert.NotNull(manifest["splitFiles"]!["textEmbedding"]);
    }

    [Fact]
    public void FreshManifestHasFilesOnly()
    {
        using var temp = new TempDirectory();

        AiPackManifestWriter.Register(temp.Path, [Llm()]);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName)))!.AsObject();
        Assert.Equal(["version", "files"], manifest.Select(p => p.Key));
    }

    [Fact]
    public async Task HashMismatchDeletesTheDownloadedFile()
    {
        using var temp = new TempDirectory();
        var corrupted = LlmBytes.ToArray();
        corrupted[10] ^= 0xFF;

        var error = await Assert.ThrowsAsync<AiPackDownloadException>(() =>
            Installer(new FakeHttpHandler(_ => FakeHttpHandler.Ok(corrupted)))
                .InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None));

        Assert.Equal(AiPackDownloadFailure.HashMismatch, error.Failure);
        Assert.Equal(AiPackComponent.LanguageModel, error.Component);
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf.partial")));
        Assert.False(File.Exists(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName)));
    }

    [Fact]
    public async Task BodyLargerThanExpectedAbortsAndDeletesThePartialFile()
    {
        using var temp = new TempDirectory();
        var oversized = LlmBytes.Concat(new byte[32]).ToArray();
        long highestRead = 0;

        var error = await Assert.ThrowsAsync<AiPackDownloadException>(() =>
            Installer(new FakeHttpHandler(_ => FakeHttpHandler.Streamed(new UnknownLengthStream(oversized, read => highestRead = read))))
                .InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None));

        Assert.Equal(AiPackDownloadFailure.SizeMismatch, error.Failure);
        Assert.True(highestRead < oversized.Length, "the installer kept reading past the expected size");
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf.partial")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf")));
    }

    [Fact]
    public async Task DeclaredContentLengthOverTheExpectedSizeIsRejectedBeforeReading()
    {
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<AiPackDownloadException>(() =>
            Installer(new FakeHttpHandler(_ => FakeHttpHandler.Ok(new byte[LlmBytes.Length + 1])))
                .InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None));

        Assert.Equal(AiPackDownloadFailure.SizeMismatch, error.Failure);
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf.partial")));
    }

    [Fact]
    public async Task ResumesAPartialFileWithAnHttpRange()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "llm"));
        File.WriteAllBytes(Path.Combine(temp.Path, "llm", "model.gguf.partial"), LlmBytes[..24]);
        var plan = Plan(temp.Path, Llm());
        var handler = new FakeHttpHandler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return FakeHttpHandler.Partial(LlmBytes, range.From!.Value);
        });

        Assert.Equal(24, plan.AlreadyDownloadedBytes);
        await Installer(handler).InstallAsync(plan, null, CancellationToken.None);

        Assert.Equal("bytes=24-", handler.Requests.Single().Headers.Range!.ToString());
        Assert.Equal(LlmBytes, File.ReadAllBytes(Path.Combine(temp.Path, "llm", "model.gguf")));
    }

    [Fact]
    public async Task RestartsFromZeroWhenTheServerIgnoresTheRange()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "llm"));
        File.WriteAllBytes(Path.Combine(temp.Path, "llm", "model.gguf.partial"), new byte[24]);

        await Installer(new FakeHttpHandler(_ => FakeHttpHandler.Ok(LlmBytes)))
            .InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None);

        Assert.Equal(LlmBytes, File.ReadAllBytes(Path.Combine(temp.Path, "llm", "model.gguf")));
    }

    [Fact]
    public async Task FollowsARedirectToTheHuggingFaceCdn()
    {
        using var temp = new TempDirectory();
        const string cdn = "https://us.aws.cdn.hf.co/xet-bridge-us/abc/def?sig=1";
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == LlmUrl
            ? FakeHttpHandler.Redirect(cdn)
            : FakeHttpHandler.Ok(LlmBytes));

        await Installer(handler).InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None);

        Assert.Equal(["huggingface.co", "us.aws.cdn.hf.co"], handler.Requests.Select(r => r.RequestUri!.Host));
        Assert.True(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf")));
    }

    [Theory]
    [InlineData("https://evil.example.com/model.gguf", AiPackDownloadFailure.HostNotAllowed)]
    [InlineData("https://huggingface.co.evil.example/model.gguf", AiPackDownloadFailure.HostNotAllowed)]
    [InlineData("http://us.aws.cdn.hf.co/model.gguf", AiPackDownloadFailure.InsecureUrl)]
    public async Task RejectsARedirectOutsideTheAllowlist(string location, AiPackDownloadFailure expected)
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == LlmUrl
            ? FakeHttpHandler.Redirect(location)
            : FakeHttpHandler.Ok(LlmBytes));

        var error = await Assert.ThrowsAsync<AiPackDownloadException>(() =>
            Installer(handler).InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None));

        Assert.Equal(expected, error.Failure);
        Assert.Single(handler.Requests);
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf")));
    }

    [Fact]
    public async Task StopsAfterTooManyRedirects()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Redirect(LlmUrl));

        var error = await Assert.ThrowsAsync<AiPackDownloadException>(() =>
            Installer(handler).InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None));

        Assert.Equal(AiPackDownloadFailure.TooManyRedirects, error.Failure);
        Assert.Equal(AiPackInstaller.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task ReportsHttpErrors()
    {
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<AiPackDownloadException>(() =>
            Installer(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
                .InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None));

        Assert.Equal(AiPackDownloadFailure.HttpStatus, error.Failure);
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task CancellationKeepsThePartialFileForResume()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var stream = new UnknownLengthStream(LlmBytes, read =>
        {
            if (read >= 20)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Installer(new FakeHttpHandler(_ => FakeHttpHandler.Streamed(stream)))
                .InstallAsync(Plan(temp.Path, Llm()), null, cancellation.Token));

        var partial = Path.Combine(temp.Path, "llm", "model.gguf.partial");
        Assert.True(File.Exists(partial));
        Assert.InRange(new FileInfo(partial).Length, 1, LlmBytes.Length - 1);
        Assert.False(File.Exists(Path.Combine(temp.Path, "llm", "model.gguf")));
    }

    [Fact]
    public async Task ReusesAnAlreadyCompleteFileWithoutTheNetwork()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "llm"));
        File.WriteAllBytes(Path.Combine(temp.Path, "llm", "model.gguf"), LlmBytes);
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no network expected"));

        await Installer(handler).InstallAsync(Plan(temp.Path, Llm()), null, CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.True(new ManifestAiPackLocator(temp.Path, temp.Path).Locate().Has(AiPackComponent.LanguageModel));
    }

    private static AiPackInstaller Installer(FakeHttpHandler handler) => new(new HttpClient(handler));

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<AiPackDownloadProgress> report) : IProgress<AiPackDownloadProgress>
    {
        public void Report(AiPackDownloadProgress value) => report(value);
    }
}
