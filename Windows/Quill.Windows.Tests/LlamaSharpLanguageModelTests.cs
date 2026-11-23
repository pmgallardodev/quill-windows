using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Settings;
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
    public async Task ModelPathOverride_ReplacesTheAiPackAndAMissingFileIsUnavailable()
    {
        var packCalls = 0;
        var overridePath = Path.Combine(Path.GetTempPath(), "quill-missing-override.gguf");
        using var model = new LlamaSharpLanguageModel(
            () =>
            {
                packCalls++;
                return @"C:\ai-pack\llm\pack-model.gguf";
            },
            new LlamaSharpOptions { ModelPath = overridePath });

        var error = await Assert.ThrowsAsync<LanguageModelUnavailableException>(() => model.GenerateTextAsync(Request, CancellationToken.None));

        Assert.Contains(overridePath, error.Message);
        Assert.Equal("quill-missing-override", model.ModelName);
        Assert.Equal(0, packCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankModelPathOverride_UsesTheAiPack(string? overridePath)
    {
        var packPath = Path.Combine(Path.GetTempPath(), "Pack-Model.gguf");
        using var model = new LlamaSharpLanguageModel(() => packPath, new LlamaSharpOptions { ModelPath = overridePath });

        Assert.Equal("pack-model", model.ModelName);
    }

    [Fact]
    public async Task AiPackLocator_WhileTheSplitModelIsBeingJoined_ReportsPreparing()
    {
        var status = new AiPackStatus(true, "/p", "2", new Dictionary<AiPackComponent, string>(), [AiPackComponent.LanguageModel])
        {
            Preparing = [new ModelPreparation(AiPackComponent.LanguageModel, ModelPreparationState.Preparing, 0.42, null)],
        };
        using var model = new LlamaSharpLanguageModel(new FixedLocator(status));

        var error = await Assert.ThrowsAsync<LanguageModelUnavailableException>(() => model.GenerateTextAsync(Request, CancellationToken.None));

        Assert.Contains("being prepared", error.Message);
        Assert.Equal("unavailable", model.ModelName);
    }

    [Fact]
    public void MissingModelReason_ExplainsAFailedJoinAndAPlainMissingModel()
    {
        var failed = AiPackStatus.NotInstalled with
        {
            Preparing = [new ModelPreparation(AiPackComponent.LanguageModel, ModelPreparationState.Failed, 0.5, "disk full")],
        };

        Assert.Contains("disk full", LlamaSharpLanguageModel.MissingModelReason(failed));
        Assert.Equal("the AI pack has no language model", LlamaSharpLanguageModel.MissingModelReason(AiPackStatus.NotInstalled));
    }

    [Theory]
    [InlineData(GpuPreference.Auto)]
    [InlineData(GpuPreference.Vulkan)]
    public void BackendPlan_GpuPreferences_UseVulkanWithCpuFallback(GpuPreference gpu)
    {
        var plan = LlamaBackendPlan.For(new LlamaSharpOptions { Gpu = gpu, GpuDevice = 1 });

        Assert.Equal(new LlamaBackendPlan(UseVulkan: true, AutoFallback: true, GpuLayers: 999, MainGpu: 1), plan);
    }

    [Fact]
    public void BackendPlan_Cpu_NeverOffloadsAndIgnoresTheDevice()
    {
        var plan = LlamaBackendPlan.For(new LlamaSharpOptions { Gpu = GpuPreference.Cpu, GpuDevice = 1 });

        Assert.Equal(new LlamaBackendPlan(UseVulkan: false, AutoFallback: true, GpuLayers: 0, MainGpu: null), plan);
    }

    [Fact]
    public void BackendPlan_DefaultsToAutoWithLlamaCppDeviceChoice()
    {
        var plan = LlamaBackendPlan.For(new LlamaSharpOptions());

        Assert.True(plan.UseVulkan);
        Assert.Null(plan.MainGpu);
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

    private sealed class FixedLocator(AiPackStatus status) : IAiPackLocator
    {
        public AiPackStatus Locate() => status;
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
