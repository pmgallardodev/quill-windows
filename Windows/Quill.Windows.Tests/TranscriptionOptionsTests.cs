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
