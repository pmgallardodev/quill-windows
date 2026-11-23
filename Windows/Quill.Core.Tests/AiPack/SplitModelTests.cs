using System.Security.Cryptography;
using Quill.Core.AiPack;
using Quill.Core.Ports;
using Xunit;

namespace Quill.Core.Tests.AiPack;

public sealed class SplitModelPlanTests
{
    [Fact]
    public void DefaultModelSplitsIntoTwoEqualPartsUnderTheAssetLimit()
    {
        var sizes = SplitModelPlan.PartSizes(2_497_281_120, SplitModelPlan.DefaultMaxPartBytes);

        Assert.Equal([1_248_640_560L, 1_248_640_560L], sizes);
        Assert.All(sizes, size => Assert.True(size < 2L * 1024 * 1024 * 1024));
    }

    [Theory]
    [InlineData(10, 10, new long[] { 10 })]
    [InlineData(10, 4, new long[] { 4, 3, 3 })]
    [InlineData(11, 4, new long[] { 4, 4, 3 })]
    [InlineData(1, 4, new long[] { 1 })]
    public void PartsAreBalancedAndNeverExceedTheMaximum(long total, long maxPart, long[] expected)
    {
        var sizes = SplitModelPlan.PartSizes(total, maxPart);

        Assert.Equal(expected, sizes);
        Assert.Equal(total, sizes.Sum());
        Assert.All(sizes, size => Assert.InRange(size, 1, maxPart));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(10, 0)]
    [InlineData(-1, 4)]
    public void InvalidSizesAreRejected(long total, long maxPart) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SplitModelPlan.PartSizes(total, maxPart));

    [Fact]
    public void PartFileNamesAreNumberedFromOne() =>
        Assert.Equal("llm/model.gguf.part2", SplitModelPlan.PartPath("llm/model.gguf", 2));
}

public sealed class SplitModelAssemblerTests
{
    [Fact]
    public async Task JoinsVerifiesRenamesAndDeletesTheParts()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(1000), partCount: 3);
        var reported = new List<long>();

        var result = await new SplitModelAssembler().AssembleAsync(job, new Progress<long>(reported.Add), CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.Assembled, result.Outcome);
        Assert.Equal(job.TargetPath, result.Path);
        Assert.Equal(Bytes(1000), File.ReadAllBytes(job.TargetPath));
        Assert.All(job.Parts, part => Assert.False(File.Exists(part.Path)));
        Assert.False(File.Exists(SplitModelAssembler.TemporaryPath(job.TargetPath)));
    }

    [Fact]
    public async Task FullHashMismatchKeepsThePartsAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(300), partCount: 2) with { Sha256 = new string('0', 64) };

        var result = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.HashMismatch, result.Outcome);
        Assert.False(result.Retryable);
        Assert.False(File.Exists(job.TargetPath));
        Assert.False(File.Exists(SplitModelAssembler.TemporaryPath(job.TargetPath)));
        Assert.All(job.Parts, part => Assert.True(File.Exists(part.Path)));
    }

    [Fact]
    public async Task CorruptPartIsNamed()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(300), partCount: 2);
        var bytes = File.ReadAllBytes(job.Parts[1].Path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(job.Parts[1].Path, bytes);

        var result = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.PartCorrupt, result.Outcome);
        Assert.Contains(Path.GetFileName(job.Parts[1].Path), result.Error);
        Assert.False(File.Exists(job.TargetPath));
    }

    [Fact]
    public async Task MissingOrTruncatedPartFailsBeforeWriting()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(300), partCount: 2);
        File.WriteAllBytes(job.Parts[0].Path, Bytes(10));

        var result = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.PartsMissing, result.Outcome);
        Assert.False(File.Exists(SplitModelAssembler.TemporaryPath(job.TargetPath)));
    }

    [Fact]
    public async Task InterruptedJoinIsRestartedFromScratch()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(500), partCount: 2);
        File.WriteAllBytes(SplitModelAssembler.TemporaryPath(job.TargetPath), Bytes(900).Reverse().ToArray());

        var result = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.Assembled, result.Outcome);
        Assert.Equal(Bytes(500), File.ReadAllBytes(job.TargetPath));
        Assert.False(File.Exists(SplitModelAssembler.TemporaryPath(job.TargetPath)));
    }

    [Fact]
    public async Task ActiveStagingReservationIsNotTruncated()
    {
        using var temp = new TempDirectory();
        var original = SplitFixture.Create(temp, Bytes(1_000_000), partCount: 2);
        var job = original with { FallbackPath = original.TargetPath };
        var firstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new SplitModelAssembler(bufferSize: 512).AssembleAsync(
            job,
            new SynchronousProgress(bytes =>
            {
                if (bytes > 0 && firstProgress.TrySetResult())
                {
                    release.Task.GetAwaiter().GetResult();
                }
            }),
            CancellationToken.None);
        await firstProgress.Task;

        var result = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);

        Assert.NotEqual(SplitAssemblyOutcome.Assembled, result.Outcome);
        release.SetResult();
        Assert.Equal(SplitAssemblyOutcome.Assembled, (await first).Outcome);
    }

    [Fact]
    public async Task StaleReservationsAreReclaimedBeforeFreeSpaceCheck()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(500), partCount: 2);
        var targetJoining = SplitModelAssembler.TemporaryPath(job.TargetPath);
        var fallbackJoining = SplitModelAssembler.TemporaryPath(job.FallbackPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fallbackJoining)!);
        File.WriteAllBytes(targetJoining, Bytes(4096));
        File.WriteAllBytes(fallbackJoining, Bytes(4096));

        var result = await new SplitModelAssembler(folder =>
        {
            var joining = Path.Combine(folder, Path.GetFileName(targetJoining));
            return File.Exists(joining) && new FileInfo(joining).Length > 0 ? 1 : long.MaxValue;
        }).AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.Assembled, result.Outcome);
        Assert.Equal(Bytes(500), File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task CancelledJoinLeavesThePartsAndNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(4000), partCount: 2);
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SplitModelAssembler(bufferSize: 512).AssembleAsync(job, progress, cancellation.Token));

        Assert.False(File.Exists(job.TargetPath));
        Assert.False(File.Exists(SplitModelAssembler.TemporaryPath(job.TargetPath)));
        Assert.All(job.Parts, part => Assert.True(File.Exists(part.Path)));

        var retry = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);
        Assert.Equal(SplitAssemblyOutcome.Assembled, retry.Outcome);
    }

    [Fact]
    public async Task NotEnoughFreeSpaceAnywhereFailsWithoutWriting()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(300), partCount: 2);

        var result = await new SplitModelAssembler(freeSpace: _ => 10).AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.NotEnoughSpace, result.Outcome);
        Assert.True(result.Retryable);
        Assert.False(File.Exists(SplitModelAssembler.TemporaryPath(job.TargetPath)));
        Assert.False(File.Exists(job.FallbackPath));
    }

    [Fact]
    public async Task ReadOnlyPackFolderAssemblesIntoTheFallbackFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Directory permissions below are POSIX modes; the logic is platform-neutral.
        }

        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(700), partCount: 2);
        var packFolder = Path.GetDirectoryName(job.TargetPath)!;
        File.SetUnixFileMode(packFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await new SplitModelAssembler().AssembleAsync(job, null, CancellationToken.None);

            Assert.Equal(SplitAssemblyOutcome.Assembled, result.Outcome);
            Assert.Equal(job.FallbackPath, result.Path);
            Assert.Equal(Bytes(700), File.ReadAllBytes(job.FallbackPath));
            Assert.False(File.Exists(job.TargetPath));
            Assert.All(job.Parts, part => Assert.True(File.Exists(part.Path)));
        }
        finally
        {
            File.SetUnixFileMode(packFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task FallbackAssemblyKeepsPortableParts()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, Bytes(700), partCount: 2);
        var packFolder = Path.GetDirectoryName(job.TargetPath)!;
        var result = await new SplitModelAssembler(folder =>
            string.Equals(folder, packFolder, StringComparison.OrdinalIgnoreCase) ? 0 : long.MaxValue)
            .AssembleAsync(job, null, CancellationToken.None);

        Assert.Equal(SplitAssemblyOutcome.Assembled, result.Outcome);
        Assert.Equal(job.FallbackPath, result.Path);
        Assert.All(job.Parts, part => Assert.True(File.Exists(part.Path)));
    }

    internal static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(index => (byte)(index * 7 % 251)).ToArray();

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

public sealed class SplitModelPreparationTests
{
    [Fact]
    public async Task PreparesOnceInTheBackgroundAndReportsReady()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, SplitModelAssemblerTests.Bytes(2000), partCount: 2);
        var preparation = new SplitModelPreparation();
        var states = new List<ModelPreparationState>();
        preparation.Changed += (_, state) => { lock (states) { states.Add(state.State); } };

        var first = preparation.Ensure(job);
        var second = preparation.Ensure(job);
        await preparation.WhenFinishedAsync(job);

        Assert.Equal(ModelPreparationState.Preparing, first.State);
        Assert.Equal(AiPackComponent.LanguageModel, first.Component);
        Assert.Equal(ModelPreparationState.Preparing, second.State);
        Assert.Equal(ModelPreparationState.Ready, preparation.Ensure(job).State);
        lock (states)
        {
            Assert.Equal(ModelPreparationState.Ready, states[^1]);
        }
    }

    [Fact]
    public async Task PermanentFailureIsNotRetried()
    {
        using var temp = new TempDirectory();
        var job = SplitFixture.Create(temp, SplitModelAssemblerTests.Bytes(200), partCount: 2) with { Sha256 = new string('0', 64) };
        var preparation = new SplitModelPreparation();

        preparation.Ensure(job);
        await preparation.WhenFinishedAsync(job);
        var failed = preparation.Ensure(job);

        Assert.Equal(ModelPreparationState.Failed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
    }

    [Fact]
    public async Task ReadyStateReassemblesWhenJoinedOutputDisappearsAndPartsReturn()
    {
        using var temp = new TempDirectory();
        var content = SplitModelAssemblerTests.Bytes(2000);
        var job = SplitFixture.Create(temp, content, partCount: 2);
        var preparation = new SplitModelPreparation();

        preparation.Ensure(job);
        await preparation.WhenFinishedAsync(job);
        Assert.Equal(ModelPreparationState.Ready, preparation.Ensure(job).State);

        File.Delete(job.TargetPath);
        SplitFixture.RestoreParts(job, content);

        var restarted = preparation.Ensure(job);
        Assert.Equal(ModelPreparationState.Preparing, restarted.State);
        await preparation.WhenFinishedAsync(job);

        Assert.Equal(ModelPreparationState.Ready, preparation.Ensure(job).State);
        Assert.Equal(content, File.ReadAllBytes(job.TargetPath));
    }
}

internal static class SplitFixture
{
    /// <summary>Writes <paramref name="content"/> as parts under "&lt;temp&gt;/pack/llm" and returns the job.</summary>
    public static SplitModelJob Create(TempDirectory temp, byte[] content, int partCount)
    {
        var folder = Path.Combine(temp.Session("pack"), "llm");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "model.gguf");
        var sizes = SplitModelPlan.PartSizes(content.Length, (content.Length + partCount - 1) / partCount);
        var parts = new List<SplitModelPart>();
        var offset = 0;
        for (var index = 0; index < sizes.Count; index++)
        {
            var slice = content.AsSpan(offset, (int)sizes[index]).ToArray();
            var path = $"{target}.part{index + 1}";
            File.WriteAllBytes(path, slice);
            parts.Add(new SplitModelPart(path, slice.Length, Hash(slice)));
            offset += slice.Length;
        }

        var fallback = Path.Combine(temp.Path, "fallback", Hash(content), "model.gguf");
        return new SplitModelJob(AiPackComponent.LanguageModel, target, fallback, content.Length, Hash(content), parts);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void RestoreParts(SplitModelJob job, byte[] content)
    {
        var offset = 0;
        foreach (var part in job.Parts)
        {
            var slice = content.AsSpan(offset, (int)part.Size).ToArray();
            File.WriteAllBytes(part.Path, slice);
            offset += slice.Length;
        }
    }
}
