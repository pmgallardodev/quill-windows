using Quill.Core.Domain;
using Quill.Core.Export;
using Quill.Core.Pipeline;
using Quill.Core.Settings;
using Quill.Core.Summaries;
using Quill.Core.Templates;
using Quill.Windows.Composition;
using Quill.Windows.Speakers;
using Quill.Windows.UI;
using Xunit;

namespace Quill.Windows.Tests.Composition;

public class AppSessionActionsTests
{
    [Fact]
    public void TemplatesListTheSettingsDefaultFirst()
    {
        var harness = new Harness { Settings = new QuillSettings { Summaries = new SummarySettings { Template = "standup" } } };

        var templates = new AppSessionActions(harness.Services()).SummaryTemplates;

        Assert.Equal("standup", templates[0]);
        Assert.Contains("general", templates);
    }

    [Fact]
    public async Task GenerateUsesTheChosenLevelAndExportsAfterwards()
    {
        var harness = new Harness();
        var actions = new AppSessionActions(harness.Services());

        await actions.GenerateSummaryAsync("s1", "general", SummaryLevel.Detailed, CancellationToken.None);

        Assert.Equal(SummaryLevel.Detailed, harness.LastOptions!.Level);
        Assert.Equal("general", harness.LastOptions.TemplateName);
        Assert.Equal(["build:s1", "export:s1"], harness.Calls);
    }

    [Fact]
    public async Task GenerateWithoutModelReportsAiPackMissing()
    {
        var harness = new Harness { LanguageModel = false };
        var actions = new AppSessionActions(harness.Services());

        await Assert.ThrowsAsync<AiPackMissingException>(() =>
            actions.GenerateSummaryAsync("s1", "general", SummaryLevel.Brief, CancellationToken.None));
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task FailedSummaryThrowsItsError()
    {
        var harness = new Harness { Outcome = SummaryOutcome.Failed("bad json") };
        var actions = new AppSessionActions(harness.Services());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.GenerateSummaryAsync("s1", "general", SummaryLevel.Brief, CancellationToken.None));
        Assert.Equal("bad json", error.Message);
        Assert.Equal(["build:s1"], harness.Calls);
    }

    [Fact]
    public async Task ReassignMapsTheSegmentStartToItsIndex()
    {
        var harness = new Harness();
        var actions = new AppSessionActions(harness.Services());

        await actions.ReassignSpeakerAsync("s1", TimeSpan.FromSeconds(7), "speaker-2", CancellationToken.None);

        Assert.True(actions.CanReassignSpeakers);
        Assert.Equal(["reassign:s1:1:speaker-2"], harness.Calls);
    }

    [Fact]
    public async Task IdentifySpeakersWithoutModelsReportsAiPackMissing()
    {
        var harness = new Harness { Speakers = new SpeakersResolveResult(SpeakersResolveState.Unavailable) };
        var actions = new AppSessionActions(harness.Services());

        await Assert.ThrowsAsync<AiPackMissingException>(() => actions.IdentifySpeakersAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task PipelineStepsSkipWhatIsNotInstalled()
    {
        var harness = new Harness { LanguageModel = false, SpeakersAvailable = false, Export = null };
        var report = await new PostTranscriptionPipeline(new AppPostTranscriptionSteps(harness.Services()))
            .RunAsync("s1", CancellationToken.None);

        Assert.All(report.Steps, step => Assert.Equal(PipelineStepStatus.Skipped, step.Status));
        Assert.Equal(["export:s1"], harness.Calls);
    }

    [Fact]
    public async Task PipelineStepsRunSpeakersSummaryAndExport()
    {
        var harness = new Harness();
        var report = await new PostTranscriptionPipeline(new AppPostTranscriptionSteps(harness.Services()))
            .RunAsync("s1", CancellationToken.None);

        Assert.False(report.HasFailures);
        Assert.Equal(["speakers:s1", "build:s1", "export:s1"], harness.Calls);
    }

    private sealed class Harness
    {
        public QuillSettings Settings { get; init; } = new();

        public bool LanguageModel { get; init; } = true;

        public bool SpeakersAvailable { get; init; } = true;

        public SummaryOutcome Outcome { get; init; } = new(SummaryStatus.Completed, new MeetingSummary(), []);

        public SpeakersResolveResult Speakers { get; init; } = new(SpeakersResolveState.Resolved);

        public MarkdownExportResult? Export { get; init; } = new("s1", MarkdownExportStatus.Created, "out.md");

        public List<string> Calls { get; } = [];

        public SummaryOptions? LastOptions { get; private set; }

        public SessionAiServices Services() => new()
        {
            Settings = () => Settings,
            Templates = () => new TemplateCatalogSnapshot(
                DefaultTemplates.All.Select(template => new TemplateEntry(template, TemplateSource.BuiltIn, null, false))
                    .Append(new TemplateEntry(DefaultTemplates.Default with { Name = "standup" }, TemplateSource.User, "standup.md", false))
                    .ToList(),
                []),
            IsLanguageModelInstalled = () => LanguageModel,
            AreSpeakersAvailable = () => SpeakersAvailable,
            BuildSummary = (id, options, _) =>
            {
                Calls.Add($"build:{id}");
                LastOptions = options;
                return Task.FromResult(Outcome);
            },
            ResolveSpeakers = (id, _) =>
            {
                Calls.Add($"speakers:{id}");
                return Task.FromResult(Speakers);
            },
            LoadFinalTranscript = (_, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), "hola", "me", Track.Microphone),
                new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9), "vale", "speaker-1", Track.System),
            ]),
            ReassignSegment = (id, index, speaker, _) =>
            {
                Calls.Add($"reassign:{id}:{index}:{speaker}");
                return Task.CompletedTask;
            },
            AutoExport = (id, _) =>
            {
                Calls.Add($"export:{id}");
                return Task.FromResult(Export);
            },
        };
    }
}
