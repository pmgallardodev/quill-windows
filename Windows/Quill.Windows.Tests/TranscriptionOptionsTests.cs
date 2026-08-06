using Quill.Windows.Infrastructure;
using Quill.Windows.Transcription;
using Xunit;

namespace Quill.Windows.Tests;

public class TranscriptionOptionsTests
{
    [Fact]
    public void ResolveThreads_UsesTheConfiguredValue_WhenItIsPositive()
    {
        Assert.Equal(12, TranscriptionOptions.ResolveThreads(12));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-4)]
    public void ResolveThreads_FallsBackToHalfTheLogicalCores_WhenTheValueIsUnusable(int? configured)
    {
        // whisper.cpp topa en cuatro hilos por defecto, que desaprovecha los CPU actuales.
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / 2), TranscriptionOptions.ResolveThreads(configured));
    }

    [Fact]
    public void ResolveThreads_NeverReturnsLessThanOne()
    {
        Assert.True(TranscriptionOptions.ResolveThreads(null) >= 1);
    }

    [Fact]
    public void ResolveModelPath_LooksInsideTheModelsFolder_ForABareFileName()
    {
        var path = ConfigStore.ResolveModelPath("ggml-large-v3-turbo-q5_0.bin");

        Assert.Equal(ConfigStore.ModelsDirectory, Path.GetDirectoryName(path));
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", Path.GetFileName(path));
    }

    [Fact]
    public void ResolveModelPath_RespectsAnAbsolutePath_SoAModelCanBeTestedWithoutCopyingIt()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "otro-modelo.bin");

        Assert.Equal(Path.GetFullPath(absolute), ConfigStore.ResolveModelPath(absolute));
    }

    [Fact]
    public void DefaultModelFile_MatchesTheOneBundledByTheProject()
    {
        // El csproj empaqueta $(QuillWhisperModel); si dejan de coincidir, la app
        // arranca buscando un archivo que el paquete portable no incluye.
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", ConfigStore.DefaultModelFile);
    }

    [Fact]
    public void FromConfig_CarriesThePromptThrough_SoWhisperGetsTheVocabulary()
    {
        var options = TranscriptionOptions.FromConfig(new TranscriptionConfig { Prompt = "Quill, Contoso, WASAPI" });

        Assert.Equal("Quill, Contoso, WASAPI", options.Prompt);
    }

    [Fact]
    public void FromConfig_TrimsThePrompt()
    {
        var options = TranscriptionOptions.FromConfig(new TranscriptionConfig { Prompt = "  Contoso  " });

        Assert.Equal("Contoso", options.Prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfig_LeavesThePromptUnset_WhenItHasNoText(string? configured)
    {
        // Un prompt en blanco se tokeniza igual y gasta contexto de whisper.cpp.
        Assert.Null(TranscriptionOptions.FromConfig(new TranscriptionConfig { Prompt = configured }).Prompt);
    }

    [Fact]
    public void FromConfig_KeepsTheRestOfTheOptions_WhenAPromptIsConfigured()
    {
        var config = new TranscriptionConfig { Prompt = "Contoso", Threads = 6, Language = "en" };

        var options = TranscriptionOptions.FromConfig(config);

        Assert.Equal(6, options.Threads);
        Assert.Equal("en", options.Language);
        Assert.Equal(ConfigStore.ResolveModelPath(ConfigStore.DefaultModelFile), options.ModelPath);
        Assert.Null(options.VadModelPath);
    }

    [Fact]
    public void FromConfig_RejectsAnUnknownEngine()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TranscriptionOptions.FromConfig(new TranscriptionConfig { Engine = "other" }));
    }

    [Fact]
    public void ResolveModelPath_RejectsRelativePathTraversal()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigStore.ResolveModelPath("..\\outside.bin"));
    }

    [Fact]
    public void TranscriptionConfig_DefaultsToNoPrompt()
    {
        Assert.Null(new TranscriptionConfig().Prompt);
    }

    [Fact]
    public void TranscriptionConfig_DefaultsToTheBundledModelWithVadOff()
    {
        var config = new TranscriptionConfig();

        Assert.Equal(ConfigStore.DefaultModelFile, config.Model);
        Assert.Equal(ConfigStore.DefaultVadModelFile, config.VadModel);
        Assert.False(config.Vad);
        Assert.Null(config.Threads);
        Assert.Equal("es", config.Language);
        Assert.True(config.Enabled);
    }
}
