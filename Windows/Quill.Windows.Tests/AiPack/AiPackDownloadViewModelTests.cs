using Quill.Core.AiPack.Download;
using Quill.Core.Ports;
using Quill.Windows.AiPack;
using Quill.Windows.UI.Settings;
using Xunit;

namespace Quill.Windows.Tests.AiPack;

public sealed class AiPackDownloadViewModelTests
{
    private static readonly AiPackCatalogEntry Llm = new(
        AiPackComponent.LanguageModel,
        "LLM",
        "llm/model.gguf",
        new Uri("https://huggingface.co/a/resolve/b/model.gguf"),
        1000,
        new string('a', 64));

    [Fact]
    public async Task RefreshOffersTheDownloadWithoutTouchingTheNetwork()
    {
        var downloader = new FakeDownloader(free: 10_000);
        var viewModel = new AiPackDownloadViewModel(downloader, () => null);

        await viewModel.RefreshAsync();

        Assert.Equal(AiPackDownloadState.Ready, viewModel.State);
        Assert.True(viewModel.CanStart);
        Assert.Equal(0, downloader.Downloads);
        Assert.Contains("ai-pack", viewModel.DestinationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotEnoughSpaceDisablesTheButton()
    {
        var viewModel = new AiPackDownloadViewModel(new FakeDownloader(free: 1000), () => null);

        await viewModel.RefreshAsync();

        Assert.Equal(AiPackDownloadState.NotEnoughSpace, viewModel.State);
        Assert.False(viewModel.CanStart);
    }

    [Fact]
    public async Task CompletedDownloadOffersCheckAgainAndRaisesEnded()
    {
        var viewModel = new AiPackDownloadViewModel(new FakeDownloader(free: 10_000), () => null);
        AiPackDestination? ended = null;
        viewModel.Ended += (_, destination) => ended = destination;
        await viewModel.RefreshAsync();

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(AiPackDownloadState.Completed, viewModel.State);
        Assert.True(viewModel.ShowCheckAgain);
        Assert.NotNull(ended);
    }

    [Fact]
    public async Task CancellationPausesForResume()
    {
        var downloader = new FakeDownloader(free: 10_000) { Fail = _ => new OperationCanceledException() };
        var viewModel = new AiPackDownloadViewModel(downloader, () => null);
        await viewModel.RefreshAsync();

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(AiPackDownloadState.Paused, viewModel.State);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task HashMismatchIsReportedAndRetryable()
    {
        var downloader = new FakeDownloader(free: 10_000)
        {
            Fail = _ => new AiPackDownloadException(AiPackDownloadFailure.HashMismatch, AiPackComponent.LanguageModel, "bad"),
        };
        var viewModel = new AiPackDownloadViewModel(downloader, () => null);
        await viewModel.RefreshAsync();

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(AiPackDownloadState.Failed, viewModel.State);
        Assert.Contains("SHA-256", viewModel.StatusText, StringComparison.Ordinal);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task StartAdmissionIsAsyncAndRejectsAStaleTypedPath()
    {
        var downloader = new FakeDownloader(free: 10_000);
        string? path = null;
        var viewModel = new AiPackDownloadViewModel(downloader, () => path);
        await viewModel.RefreshAsync();
        var delayed = new TaskCompletionSource<AiPackDownloadOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.PrepareOverride = (_, _) => delayed.Task;

        var start = viewModel.StartAsync(CancellationToken.None);

        Assert.False(viewModel.CanStart);
        path = "typed-while-hashing";
        await viewModel.StartAsync(CancellationToken.None);
        Assert.Equal(2, downloader.Preparations);

        delayed.SetResult(downloader.Offer);
        await start;

        Assert.Equal(0, downloader.Downloads);
        Assert.Equal(AiPackDownloadState.Ready, viewModel.State);
        Assert.True(viewModel.CanStart);
    }

    private sealed class FakeDownloader(long free) : IAiPackDownloader
    {
        public int Downloads { get; private set; }

        public int Preparations { get; private set; }

        public AiPackDownloadOffer Offer { get; private set; } = null!;

        public Func<string?, CancellationToken, Task<AiPackDownloadOffer>>? PrepareOverride { get; set; }

        public Func<AiPackDownloadOffer, Exception?> Fail { get; init; } = _ => null;

        public Task<AiPackDownloadOffer> PrepareAsync(string? packPath, CancellationToken cancellationToken)
        {
            Preparations++;
            if (PrepareOverride is { } prepare)
            {
                return prepare(packPath, cancellationToken);
            }

            var root = Path.Combine(Path.GetTempPath(), "quill-tests", "ai-pack");
            var plan = AiPackDownloadPlan.Create(new AiPackCatalog([Llm]), AiPackStatus.NotInstalled, root);
            Offer = new AiPackDownloadOffer(new AiPackDestination(root, false), plan, free);
            return Task.FromResult(Offer);
        }

        public Task DownloadAsync(AiPackDownloadOffer offer, IProgress<AiPackDownloadProgress> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return Fail(offer) is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
    }
}
