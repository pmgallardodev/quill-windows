using Quill.Core.Domain;
using Quill.Core.Pipeline;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Xunit;

namespace Quill.Core.Tests.Pipeline;

public sealed class TranscriptSegmentLocatorTests
{
    private static readonly IReadOnlyList<TranscriptSegment> Segments =
    [
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "hola", "me", Track.Microphone),
        new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), "buenas", "speaker-1", Track.System),
        new(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(12), "vale", "speaker-1", Track.System),
    ];

    [Fact]
    public void FindsTheSegmentThatStartsAtTheGivenTime()
    {
        Assert.Equal(1, TranscriptSegmentLocator.IndexOf(Segments, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ToleratesMillisecondRounding()
    {
        Assert.Equal(2, TranscriptSegmentLocator.IndexOf(Segments, TimeSpan.FromMilliseconds(9_000.6)));
    }

    [Fact]
    public void ReturnsMinusOneWhenNoSegmentStartsThere()
    {
        Assert.Equal(-1, TranscriptSegmentLocator.IndexOf(Segments, TimeSpan.FromSeconds(3)));
    }
}

public sealed class LiveSummaryPolicyTests
{
    [Theory]
    [InlineData(null, true, true, true)]
    [InlineData(null, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    public void FollowsSettingThenGpuAndNeedsTheModel(bool? liveEnabled, bool gpu, bool model, bool expected)
    {
        var settings = new SummarySettings { LiveEnabled = liveEnabled };

        Assert.Equal(expected, LiveSummaryPolicy.ShouldRun(settings, gpu, model));
    }

    [Fact]
    public void CpuPreferenceMeansNoGpu()
    {
        Assert.False(LiveSummaryPolicy.GpuUsable(new AiSettings { Gpu = GpuPreference.Cpu }, probe: () => true));
        Assert.True(LiveSummaryPolicy.GpuUsable(new AiSettings { Gpu = GpuPreference.Auto }, probe: () => true));
        Assert.False(LiveSummaryPolicy.GpuUsable(new AiSettings(), probe: () => throw new IOException("no driver")));
    }
}

public sealed class AiPackOverridesTests
{
    private static readonly AiPackStatus Pack = new(
        true,
        "/pack",
        "1.0.0",
        new Dictionary<AiPackComponent, string>
        {
            [AiPackComponent.LanguageModel] = "/pack/llm.gguf",
            [AiPackComponent.SpeakerEmbedding] = "/pack/emb.onnx",
        },
        [AiPackComponent.LiveWhisper, AiPackComponent.DiarizationSegmentation, AiPackComponent.TextEmbedding]);

    [Fact]
    public void ExistingOverrideReplacesThePackFile()
    {
        var status = AiPackOverrides.Apply(Pack, "/models/other.gguf", "/models/small.bin", _ => true);

        Assert.Equal("/models/other.gguf", status.PathOf(AiPackComponent.LanguageModel));
        Assert.Equal("/models/small.bin", status.PathOf(AiPackComponent.LiveWhisper));
        Assert.DoesNotContain(AiPackComponent.LiveWhisper, status.Missing);
        Assert.Equal("/pack/emb.onnx", status.PathOf(AiPackComponent.SpeakerEmbedding));
    }

    [Fact]
    public void MissingOrBlankOverrideKeepsThePack()
    {
        var status = AiPackOverrides.Apply(Pack, "/models/gone.gguf", " ", _ => false);

        Assert.Same(Pack, status);
    }

    [Fact]
    public void OverrideWorksWithoutAnInstalledPack()
    {
        var status = AiPackOverrides.Apply(AiPackStatus.NotInstalled, "/models/x.gguf", null, _ => true);

        Assert.True(status.IsInstalled);
        Assert.True(status.Has(AiPackComponent.LanguageModel));
        Assert.Contains(AiPackComponent.LiveWhisper, status.Missing);
    }
}
