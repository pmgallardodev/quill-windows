using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Quill.Windows.Summaries;
using Xunit;

namespace Quill.Windows.Tests;

public class LlamaSharpLanguageModelTests
{
    private static readonly LanguageModelRequest Request = new("system", "user");

    [Fact]
    public async Task GenerateJson_WithoutModelInThePack_ThrowsUnavailable()
    {
        using var model = new LlamaSharpLanguageModel(() => null);

        await Assert.ThrowsAsync<LanguageModelUnavailableException>(() =>
            model.GenerateJsonAsync(Request, MeetingSummarySchema.For(SummaryKind.Live), CancellationToken.None));
    }

    [Fact]
    public async Task Generate_WithMissingFile_ThrowsUnavailableAndRetriesResolutionNextTime()
    {
        var calls = 0;
        using var model = new LlamaSharpLanguageModel(() =>
        {
            calls++;
            return Path.Combine(Path.GetTempPath(), "quill-missing-model.gguf");
        });

        await Assert.ThrowsAsync<LanguageModelUnavailableException>(() => model.GenerateTextAsync(Request, CancellationToken.None));
        await Assert.ThrowsAsync<LanguageModelUnavailableException>(() => model.GenerateTextAsync(Request, CancellationToken.None));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void ModelName_ComesFromTheFileName()
    {
        using var model = new LlamaSharpLanguageModel(() => @"C:\ai-pack\llm\Qwen3-4B-Instruct-2507-Q4_K_M.gguf");

        Assert.Equal("qwen3-4b-instruct-2507-q4_k_m", model.ModelName);
        Assert.Equal(8192, model.ContextTokens);
    }

    [Fact]
    public async Task SummariesModule_WithoutAiPack_ReportsAiPackMissingFromCatchUp()
    {
        using var temp = new TempDirectory();
        using var module = new SummariesModule(
            new Quill.Core.AiPack.ManifestAiPackLocator(temp.Path),
            new InlineScheduler(),
            new Quill.Core.Storage.FileSessionStore(temp.Path));
        TranscriptSegment[] transcript = [new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Hola a todos.", SpeakerIds.Me, Track.Microphone)];

        var outcome = await module.CatchUp.SummarizeAsync(transcript, new SummaryOptions());

        Assert.False(module.IsLanguageModelInstalled);
        Assert.Equal(SummaryStatus.AiPackMissing, outcome.Status);
        Assert.Equal(6, module.Templates.Count);
    }

    private sealed class InlineScheduler : IInferenceScheduler
    {
        public int PendingCount => 0;

        public Task<T> RunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default) =>
            job(cancellationToken);

        public async Task<(bool Ran, T? Result)> TryRunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default) =>
            (true, await job(cancellationToken));
    }
}
