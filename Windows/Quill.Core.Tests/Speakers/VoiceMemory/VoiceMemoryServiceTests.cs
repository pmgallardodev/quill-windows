using Quill.Core.Speakers.VoiceMemory;
using Xunit;

namespace Quill.Core.Tests.Speakers.VoiceMemory;

public sealed class VoiceMemoryServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private VoiceMemoryService Memory() =>
        new(new FileVoiceLibraryStore(_temp.Path), clock: new FixedClock(T0));

    [Fact]
    public async Task MissingFileIsAnEmptyLibrary()
    {
        var library = await new FileVoiceLibraryStore(_temp.Path).LoadAsync(CancellationToken.None);

        Assert.Empty(library.Voices);
        Assert.False(library.AutoApply);
    }

    [Fact]
    public async Task VoicesRoundTripThroughVoicesJsonUnderTheRecordingsRoot()
    {
        var store = new FileVoiceLibraryStore(_temp.Path);
        var library = VoiceLibrary.Empty
            .Learn("Juan", "s1", "speaker-1", [1f, 0f], T0)
            .LearnOwner("s1", [0f, 2f], T0)
            .WithAutoApply(true);

        await store.SaveAsync(library, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_temp.Path, FileVoiceLibraryStore.FileName)));
        Assert.True(loaded.AutoApply);
        var juan = loaded.Voices.Single(v => v.Name == "Juan");
        Assert.Equal(library.Voices.Single(v => v.Name == "Juan").Id, juan.Id);
        Assert.Equal(["s1"], juan.Sessions);
        Assert.Equal(T0, juan.LastSeen);
        Assert.Equal([1f, 0f], juan.Embedding);
        Assert.Equal([0f, 1f], loaded.Owner!.Embedding);
    }

    [Fact]
    public async Task CorruptFileIsAnEmptyLibrary()
    {
        File.WriteAllText(Path.Combine(_temp.Path, FileVoiceLibraryStore.FileName), "{ not json");

        Assert.Empty((await new FileVoiceLibraryStore(_temp.Path).LoadAsync(CancellationToken.None)).Voices);
    }

    [Fact]
    public async Task NamingASpeakerLearnsAndClearingTheNameUnlearns()
    {
        var memory = Memory();

        await memory.LearnAsync("s1", "speaker-1", "Juan", [1f, 0f], CancellationToken.None);
        Assert.Equal(["Juan"], (await memory.LoadAsync(CancellationToken.None)).Voices.Select(v => v.Name));

        await memory.LearnAsync("s1", "speaker-1", null, [1f, 0f], CancellationToken.None);
        Assert.Empty((await memory.LoadAsync(CancellationToken.None)).Voices);
    }

    [Fact]
    public async Task NamingTheOwnerOrASpeakerWithoutEmbeddingLearnsNothing()
    {
        var memory = Memory();

        await memory.LearnAsync("s1", "me", "Pablo", [1f, 0f], CancellationToken.None);
        await memory.LearnAsync("s1", "speaker-1", "Juan", null, CancellationToken.None);

        Assert.Empty((await memory.LoadAsync(CancellationToken.None)).Voices);
    }

    [Fact]
    public async Task ManagementOperationsPersist()
    {
        var memory = Memory();
        await memory.LearnAsync("s1", "speaker-1", "Juan", [1f, 0f], CancellationToken.None);
        await memory.LearnAsync("s1", "speaker-2", "Ana", [0f, 1f], CancellationToken.None);
        var juan = (await memory.LoadAsync(CancellationToken.None)).Voices.Single(v => v.Name == "Juan").Id;

        await memory.RenameAsync(juan, "Juan Pérez", CancellationToken.None);
        await memory.SetAutoApplyAsync(true, CancellationToken.None);
        var reloaded = await new FileVoiceLibraryStore(_temp.Path).LoadAsync(CancellationToken.None);
        Assert.Contains(reloaded.Voices, v => v.Name == "Juan Pérez");
        Assert.True(reloaded.AutoApply);

        await memory.ForgetAsync(juan, CancellationToken.None);
        Assert.Equal(["Ana"], (await memory.LoadAsync(CancellationToken.None)).Voices.Select(v => v.Name));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
