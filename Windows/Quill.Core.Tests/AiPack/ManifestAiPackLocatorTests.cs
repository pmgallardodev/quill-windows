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
}
