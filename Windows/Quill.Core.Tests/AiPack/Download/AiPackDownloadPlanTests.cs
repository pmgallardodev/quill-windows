using Quill.Core.AiPack;
using Quill.Core.AiPack.Download;
using Quill.Core.Ports;
using Xunit;
using static Quill.Core.Tests.AiPack.Download.DownloadFixtures;

namespace Quill.Core.Tests.AiPack.Download;

public sealed class AiPackDownloadPlanTests
{
    [Fact]
    public void PlansEveryComponentWhenThePackIsMissing()
    {
        using var temp = new TempDirectory();

        var plan = Plan(temp.Path, Llm(), Whisper());

        Assert.Equal([AiPackComponent.LanguageModel, AiPackComponent.LiveWhisper], plan.Items.Select(i => i.Component));
        Assert.Equal(LlmBytes.Length + WhisperBytes.Length, plan.TotalBytes);
        Assert.Equal(0, plan.AlreadyDownloadedBytes);
        Assert.Equal(plan.TotalBytes, plan.RemainingBytes);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void SkipsComponentsTheLocatorAlreadyFoundInTheSameRoot()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "whisper"));
        File.WriteAllBytes(Path.Combine(temp.Path, "whisper", "ggml.bin"), WhisperBytes);
        File.WriteAllText(Path.Combine(temp.Path, ManifestAiPackLocator.ManifestFileName), """
            { "version": "1", "files": { "liveWhisper": "whisper/ggml.bin" } }
            """);
        var status = new ManifestAiPackLocator(temp.Path, temp.Path).Locate();

        var plan = AiPackDownloadPlan.Create(new AiPackCatalog([Llm(), Whisper()]), status, temp.Path);

        Assert.Equal([AiPackComponent.LanguageModel], plan.Items.Select(i => i.Component));
    }

    [Fact]
    public void IgnoresAStatusFromAnotherRoot()
    {
        using var temp = new TempDirectory();
        var elsewhere = AiPackStatus.NotInstalled with
        {
            IsInstalled = true,
            Root = Path.Combine(temp.Path, "other"),
            Paths = new Dictionary<AiPackComponent, string> { [AiPackComponent.LiveWhisper] = "x" },
        };

        var plan = AiPackDownloadPlan.Create(new AiPackCatalog([Llm(), Whisper()]), elsewhere, temp.Path);

        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public void SkipsAComponentWhoseSplitPartsAreBeingJoined()
    {
        using var temp = new TempDirectory();
        var status = AiPackStatus.NotInstalled with
        {
            IsInstalled = true,
            Root = temp.Path,
            Preparing = [new ModelPreparation(AiPackComponent.LanguageModel, ModelPreparationState.Preparing, 0.3, null)],
        };

        var plan = AiPackDownloadPlan.Create(new AiPackCatalog([Llm(), Whisper()]), status, temp.Path);

        Assert.Equal([AiPackComponent.LiveWhisper], plan.Items.Select(i => i.Component));
    }

    [Fact]
    public async Task CountsPartialAndCompleteFilesAsAlreadyDownloaded()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "llm"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "whisper"));
        File.WriteAllBytes(Path.Combine(temp.Path, "llm", "model.gguf.partial"), new byte[10]);
        File.WriteAllBytes(Path.Combine(temp.Path, "whisper", "ggml.bin"), WhisperBytes);

        var plan = await AiPackDownloadPlan.CreateAsync(new AiPackCatalog([Llm(), Whisper()]), AiPackStatus.NotInstalled, temp.Path);

        Assert.Equal(10 + WhisperBytes.Length, plan.AlreadyDownloadedBytes);
        Assert.Equal(LlmBytes.Length - 10, plan.RemainingBytes);
    }

    [Fact]
    public async Task DoesNotCreditSameSizeFileWithIncorrectPinnedHash()
    {
        using var temp = new TempDirectory();
        var entry = Llm();
        var target = AiPackDownloadPlan.TargetPath(temp.Path, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, new byte[entry.Size]);
        File.WriteAllBytes(target + AiPackDownloadPlan.PartialSuffix, LlmBytes[..8]);

        var plan = await AiPackDownloadPlan.CreateAsync(new AiPackCatalog([entry]), AiPackStatus.NotInstalled, temp.Path);

        Assert.Equal(8, plan.AlreadyDownloadedBytes);
        var remaining = entry.Size - 8;
        Assert.Equal(remaining + (long)Math.Ceiling(remaining * AiPackDownloadPlan.FreeSpaceMargin), plan.RequiredFreeBytes);
    }

    [Fact]
    public async Task CreditsVerifiedSameSizeOrphanWithoutAdditionalFreeSpace()
    {
        using var temp = new TempDirectory();
        var entry = Llm();
        var target = AiPackDownloadPlan.TargetPath(temp.Path, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, LlmBytes);

        var plan = await AiPackDownloadPlan.CreateAsync(new AiPackCatalog([entry]), AiPackStatus.NotInstalled, temp.Path);

        Assert.Equal(entry.Size, plan.AlreadyDownloadedBytes);
        Assert.Equal(0, plan.RequiredFreeBytes);
    }

    [Fact]
    public void RequiresTenPercentMoreFreeSpaceThanTheRemainingBytes()
    {
        using var temp = new TempDirectory();
        var plan = AiPackDownloadPlan.Create(
            new AiPackCatalog([Llm(size: 1000)]),
            AiPackStatus.NotInstalled,
            temp.Path);

        Assert.Equal(1100, plan.RequiredFreeBytes);
        Assert.True(plan.HasEnoughSpace(1100));
        Assert.False(plan.HasEnoughSpace(1099));
    }

    [Fact]
    public void NothingToDownloadWhenEverythingIsInstalled()
    {
        using var temp = new TempDirectory();
        var status = new AiPackStatus(
            true,
            temp.Path,
            "1",
            new Dictionary<AiPackComponent, string> { [AiPackComponent.LanguageModel] = "x" },
            []);

        var plan = AiPackDownloadPlan.Create(new AiPackCatalog([Llm()]), status, temp.Path);

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.RequiredFreeBytes);
    }
}
