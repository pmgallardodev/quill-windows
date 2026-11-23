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
    public void RefreshOffersTheDownloadWithoutTouchingTheNetwork()
    {
        var downloader = new FakeDownloader(free: 10_000);
        var viewModel = new AiPackDownloadViewModel(downloader, () => null);

        viewModel.Refresh();

        Assert.Equal(AiPackDownloadState.Ready, viewModel.State);
        Assert.True(viewModel.CanStart);
        Assert.Equal(0, downloader.Downloads);
        Assert.Contains("ai-pack", viewModel.DestinationText, StringComparison.Ordinal);
    }

    [Fact]
    public void NotEnoughSpaceDisablesTheButton()
    {
        var viewModel = new AiPackDownloadViewModel(new FakeDownloader(free: 1000), () => null);

        viewModel.Refresh();

        Assert.Equal(AiPackDownloadState.NotEnoughSpace, viewModel.State);
        Assert.False(viewModel.CanStart);
    }

    [Fact]
    public async Task CompletedDownloadOffersCheckAgainAndRaisesEnded()
    {
        var viewModel = new AiPackDownloadViewModel(new FakeDownloader(free: 10_000), () => null);
        AiPackDestination? ended = null;
        viewModel.Ended += (_, destination) => ended = destination;
        viewModel.Refresh();

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
        viewModel.Refresh();

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
        viewModel.Refresh();

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(AiPackDownloadState.Failed, viewModel.State);
        Assert.Contains("SHA-256", viewModel.StatusText, StringComparison.Ordinal);
        Assert.True(viewModel.CanStart);
    }

    private sealed class FakeDownloader(long free) : IAiPackDownloader
    {
        public int Downloads { get; private set; }

        public Func<AiPackDownloadOffer, Exception?> Fail { get; init; } = _ => null;

        public AiPackDownloadOffer Prepare(string? packPath)
        {
            var root = Path.Combine(Path.GetTempPath(), "quill-tests", "ai-pack");
            var plan = AiPackDownloadPlan.Create(new AiPackCatalog([Llm]), AiPackStatus.NotInstalled, root);
            return new AiPackDownloadOffer(new AiPackDestination(root, false), plan, free);
        }

        public Task DownloadAsync(AiPackDownloadOffer offer, IProgress<AiPackDownloadProgress> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return Fail(offer) is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
    }
}
