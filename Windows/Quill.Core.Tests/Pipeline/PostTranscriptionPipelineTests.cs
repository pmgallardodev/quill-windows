using Quill.Core.Pipeline;
using Xunit;

namespace Quill.Core.Tests.Pipeline;

public sealed class PostTranscriptionPipelineTests
{
    [Fact]
    public async Task RunsSpeakersThenSummaryThenExport()
    {
        var steps = new FakeSteps();
        var pipeline = new PostTranscriptionPipeline(steps);

        var report = await pipeline.RunAsync("s1", CancellationToken.None);

        Assert.Equal(["speakers:s1", "summary:s1", "export:s1"], steps.Calls);
        Assert.Equal("s1", report.SessionId);
        Assert.False(report.HasFailures);
        Assert.All(report.Steps, step => Assert.Equal(PipelineStepStatus.Completed, step.Status));
    }

    [Fact]
    public async Task SpeakerFailureStillSummarizesAndExports()
    {
        var steps = new FakeSteps { Speakers = _ => throw new InvalidOperationException("sherpa crashed") };
        var pipeline = new PostTranscriptionPipeline(steps);

        var report = await pipeline.RunAsync("s1", CancellationToken.None);

        Assert.Equal(["summary:s1", "export:s1"], steps.Calls);
        var speakers = report.Of(PostTranscriptionStep.Speakers);
        Assert.Equal(PipelineStepStatus.Failed, speakers!.Status);
        Assert.Equal("sherpa crashed", speakers.Detail);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public async Task FailedSummarySkipsExport()
    {
        var steps = new FakeSteps { Summary = _ => PipelineStepResult.Failed(PostTranscriptionStep.Summary, "bad json") };
        var pipeline = new PostTranscriptionPipeline(steps);

        var report = await pipeline.RunAsync("s1", CancellationToken.None);

        Assert.Equal(["speakers:s1", "summary:s1"], steps.Calls);
        Assert.Equal(PipelineStepStatus.Skipped, report.Of(PostTranscriptionStep.Export)!.Status);
    }

    [Fact]
    public async Task SkippedSummaryStillExportsTheTranscript()
    {
        var steps = new FakeSteps { Summary = _ => PipelineStepResult.Skipped(PostTranscriptionStep.Summary, "no model") };
        var pipeline = new PostTranscriptionPipeline(steps);

        var report = await pipeline.RunAsync("s1", CancellationToken.None);

        Assert.Equal(["speakers:s1", "summary:s1", "export:s1"], steps.Calls);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new FakeSteps
        {
            Speakers = _ =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return PipelineStepResult.Completed(PostTranscriptionStep.Speakers);
            },
        };
        var pipeline = new PostTranscriptionPipeline(steps);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync("s1", cancellation.Token));
        Assert.Empty(steps.Calls);
    }

    [Fact]
    public async Task RunsOneSessionAtATimeAndJoinsDuplicateRequests()
    {
        var release = new TaskCompletionSource();
        var steps = new FakeSteps();
        steps.SpeakersAsync = async id =>
        {
            if (id == "s1")
            {
                await release.Task;
            }

            return PipelineStepResult.Completed(PostTranscriptionStep.Speakers);
        };
        var pipeline = new PostTranscriptionPipeline(steps);

        var first = pipeline.RunAsync("s1", CancellationToken.None);
        var duplicate = pipeline.RunAsync("s1", CancellationToken.None);
        var second = pipeline.RunAsync("s2", CancellationToken.None);
        await Task.Delay(50);

        Assert.Same(first, duplicate);
        Assert.Empty(steps.Calls);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["speakers:s1", "summary:s1", "export:s1", "speakers:s2", "summary:s2", "export:s2"], steps.Calls);
    }

    private sealed class FakeSteps : IPostTranscriptionSteps
    {
        private readonly object _gate = new();

        public List<string> Calls { get; } = [];

        public Func<string, PipelineStepResult> Speakers { get; set; } = _ => PipelineStepResult.Completed(PostTranscriptionStep.Speakers);

        public Func<string, Task<PipelineStepResult>>? SpeakersAsync { get; set; }

        public Func<string, PipelineStepResult> Summary { get; set; } = _ => PipelineStepResult.Completed(PostTranscriptionStep.Summary);

        public async Task<PipelineStepResult> ResolveSpeakersAsync(string sessionId, CancellationToken cancellationToken)
        {
            var result = SpeakersAsync is not null ? await SpeakersAsync(sessionId) : Speakers(sessionId);
            Record($"speakers:{sessionId}");
            return result;
        }

        public Task<PipelineStepResult> SummarizeAsync(string sessionId, CancellationToken cancellationToken)
        {
            var result = Summary(sessionId);
            Record($"summary:{sessionId}");
            return Task.FromResult(result);
        }

        public Task<PipelineStepResult> ExportAsync(string sessionId, CancellationToken cancellationToken)
        {
            Record($"export:{sessionId}");
            return Task.FromResult(PipelineStepResult.Completed(PostTranscriptionStep.Export));
        }

        private void Record(string call)
        {
            lock (_gate)
            {
                Calls.Add(call);
            }
        }
    }
}
