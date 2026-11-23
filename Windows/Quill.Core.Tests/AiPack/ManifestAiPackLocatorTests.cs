using Quill.Core.AiPack;
using Quill.Core.Ports;
using Xunit;

namespace Quill.Core.Tests.AiPack;

public sealed class ManifestAiPackLocatorTests
{
    [Fact]
    public void MissingPackIsNotInstalled()
    {
        using var temp = new TempDirectory();

        var status = new ManifestAiPackLocator(temp.Path).Locate();

        Assert.False(status.IsInstalled);
        Assert.Equal(Enum.GetValues<AiPackComponent>(), status.Missing);
        Assert.Null(status.PathOf(AiPackComponent.LanguageModel));
    }

    [Fact]
    public void FindsPackNextToTheAppAndReportsMissingFiles()
    {
        using var temp = new TempDirectory();
        var pack = temp.Session(ManifestAiPackLocator.DefaultFolderName);
        Directory.CreateDirectory(Path.Combine(pack, "llm"));
        File.WriteAllText(Path.Combine(pack, "llm", "model.gguf"), "x");
        File.WriteAllText(Path.Combine(pack, ManifestAiPackLocator.ManifestFileName), """
            {
              "version": "1.0.0",
              "files": {
                "languageModel": "llm/model.gguf",
                "liveWhisper": "whisper/ggml-small.bin",
                "speakerEmbedding": "../outside.onnx"
              }
            }
            """);

        var status = new ManifestAiPackLocator(temp.Path).Locate();

        Assert.True(status.IsInstalled);
        Assert.Equal("1.0.0", status.Version);
        Assert.Equal(Path.Combine(pack, "llm", "model.gguf"), status.PathOf(AiPackComponent.LanguageModel));
        Assert.True(status.Has(AiPackComponent.LanguageModel));
        Assert.Contains(AiPackComponent.LiveWhisper, status.Missing);
        Assert.Contains(AiPackComponent.SpeakerEmbedding, status.Missing);
        Assert.Contains(AiPackComponent.TextEmbedding, status.Missing);
    }

    [Fact]
    public void OverridePathWinsAndCorruptManifestIsNotInstalled()
    {
        using var temp = new TempDirectory();
        var custom = temp.Session("custom");
        File.WriteAllText(Path.Combine(custom, ManifestAiPackLocator.ManifestFileName), "{ nope");

        var status = new ManifestAiPackLocator(temp.Path, custom).Locate();

        Assert.False(status.IsInstalled);
        Assert.Equal(custom, status.Root);
    }

    [Fact]
    public async Task SplitModelIsPreparingUntilJoinedThenFound()
    {
        using var temp = new TempDirectory();
        var content = SplitModelAssemblerTests.Bytes(3000);
        var pack = WriteSplitPack(temp, content, writeSecondPart: true);
        var preparation = new SplitModelPreparation();
        var locator = new ManifestAiPackLocator(temp.Path, null, Path.Combine(temp.Path, "fallback"), preparation);

        var preparing = locator.Locate();

        Assert.True(preparing.IsInstalled);
        Assert.Contains(AiPackComponent.LanguageModel, preparing.Missing);
        Assert.Null(preparing.PathOf(AiPackComponent.LanguageModel));
        var state = Assert.IsType<ModelPreparation>(preparing.PreparationOf(AiPackComponent.LanguageModel));
        Assert.Equal(ModelPreparationState.Preparing, state.State);

        await preparation.WhenFinishedAsync(AiPackComponent.LanguageModel);
        var ready = locator.Locate();

        var model = Path.Combine(pack, "llm", "model.gguf");
        Assert.Equal(model, ready.PathOf(AiPackComponent.LanguageModel));
        Assert.Null(ready.PreparationOf(AiPackComponent.LanguageModel));
        Assert.Equal(content, File.ReadAllBytes(model));
        Assert.False(File.Exists(model + ".part1"));
    }

    [Fact]
    public void SplitModelWithAMissingPartIsMissingAndNotPrepared()
    {
        using var temp = new TempDirectory();
        WriteSplitPack(temp, SplitModelAssemblerTests.Bytes(3000), writeSecondPart: false);
        var preparation = new SplitModelPreparation();

        var status = new ManifestAiPackLocator(temp.Path, null, Path.Combine(temp.Path, "fallback"), preparation).Locate();

        Assert.Contains(AiPackComponent.LanguageModel, status.Missing);
        Assert.Null(status.PreparationOf(AiPackComponent.LanguageModel));
    }

    [Fact]
    public void ModelAlreadyJoinedIntoTheFallbackFolderIsUsed()
    {
        using var temp = new TempDirectory();
        var content = SplitModelAssemblerTests.Bytes(3000);
        WriteSplitPack(temp, content, writeSecondPart: true);
        var fallbackRoot = Path.Combine(temp.Path, "fallback");
        var joined = Path.Combine(fallbackRoot, SplitFixture.Hash(content), "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(joined)!);
        File.WriteAllBytes(joined, content);

        var status = new ManifestAiPackLocator(temp.Path, null, fallbackRoot, new SplitModelPreparation()).Locate();

        Assert.Equal(joined, status.PathOf(AiPackComponent.LanguageModel));
        Assert.Null(status.PreparationOf(AiPackComponent.LanguageModel));
    }

    [Theory]
    [InlineData("../outside.part1", 1500)]
    [InlineData("llm/model.gguf.part1", 1499)]
    public void InvalidSplitEntryIsIgnored(string firstPart, long firstSize)
    {
        using var temp = new TempDirectory();
        var pack = temp.Session(ManifestAiPackLocator.DefaultFolderName);
        File.WriteAllText(Path.Combine(pack, ManifestAiPackLocator.ManifestFileName), $$"""
            {
              "version": "2.0.0",
              "files": { "languageModel": "llm/model.gguf" },
              "splitFiles": {
                "languageModel": {
                  "size": 3000,
                  "sha256": "{{new string('a', 64)}}",
                  "parts": [
                    { "file": "{{firstPart}}", "size": {{firstSize}} },
                    { "file": "llm/model.gguf.part2", "size": 1500 }
                  ]
                }
              }
            }
            """);
        var preparation = new SplitModelPreparation();

        var status = new ManifestAiPackLocator(temp.Path, null, Path.Combine(temp.Path, "fallback"), preparation).Locate();

        Assert.True(status.IsInstalled);
        Assert.Contains(AiPackComponent.LanguageModel, status.Missing);
        Assert.Null(status.PreparationOf(AiPackComponent.LanguageModel));
    }

    private static string WriteSplitPack(TempDirectory temp, byte[] content, bool writeSecondPart)
    {
        var pack = temp.Session(ManifestAiPackLocator.DefaultFolderName);
        Directory.CreateDirectory(Path.Combine(pack, "llm"));
        var half = content.Length / 2;
        var first = content[..half];
        var second = content[half..];
        File.WriteAllBytes(Path.Combine(pack, "llm", "model.gguf.part1"), first);
        if (writeSecondPart)
        {
            File.WriteAllBytes(Path.Combine(pack, "llm", "model.gguf.part2"), second);
        }

        File.WriteAllText(Path.Combine(pack, ManifestAiPackLocator.ManifestFileName), $$"""
            {
              "version": "2.0.0",
              "files": { "languageModel": "llm/model.gguf" },
              "splitFiles": {
                "languageModel": {
                  "size": {{content.Length}},
                  "sha256": "{{SplitFixture.Hash(content)}}",
                  "parts": [
                    { "file": "llm/model.gguf.part1", "size": {{first.Length}}, "sha256": "{{SplitFixture.Hash(first)}}" },
                    { "file": "llm/model.gguf.part2", "size": {{second.Length}}, "sha256": "{{SplitFixture.Hash(second)}}" }
                  ]
                }
              }
            }
            """);
        return pack;
    }
}
