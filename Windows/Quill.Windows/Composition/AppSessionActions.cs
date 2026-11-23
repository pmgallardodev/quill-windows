using System.Diagnostics;
using Quill.Core.Domain;
using Quill.Core.Pipeline;
using Quill.Core.Settings;
using Quill.Core.Summaries;
using Quill.Core.Templates;
using Quill.Windows.Speakers;
using Quill.Windows.UI;

namespace Quill.Windows.Composition;

/// <summary>
/// What the AI actions need from the modules, as plain delegates so the mapping in
/// <see cref="AppSessionActions"/> and <see cref="AppPostTranscriptionSteps"/> is testable
/// without models. <see cref="QuillServices"/> builds the real one.
/// </summary>
internal sealed record SessionAiServices
{
    public required Func<QuillSettings> Settings { get; init; }

    /// <summary>Built-ins merged with the user's templates folder (reads the disk).</summary>
    public required Func<TemplateCatalogSnapshot> Templates { get; init; }

    public required Func<bool> IsLanguageModelInstalled { get; init; }

    public required Func<bool> AreSpeakersAvailable { get; init; }

    /// <summary>FinalSummaryBuilder.BuildAsync: summarizes and saves summary.json.</summary>
    public required Func<string, SummaryOptions, CancellationToken, Task<SummaryOutcome>> BuildSummary { get; init; }

    public required Func<string, CancellationToken, Task<SpeakersResolveResult>> ResolveSpeakers { get; init; }

    public required Func<string, CancellationToken, Task<IReadOnlyList<TranscriptSegment>>> LoadFinalTranscript { get; init; }

    /// <summary>SpeakersModule.ReassignAsync(sessionId, segmentIndex, speakerId).</summary>
    public required Func<string, int, string, CancellationToken, Task> ReassignSegment { get; init; }

    /// <summary>Auto-export after a final summary; null result when export is off.</summary>
    public required Func<string, CancellationToken, Task<Quill.Core.Export.MarkdownExportResult?>> AutoExport { get; init; }

    /// <summary>summary.state.json says the transcript changed after the summary; null: never outdated.</summary>
    public Func<string, CancellationToken, Task<bool>>? IsSummaryOutdated { get; init; }
}

/// <summary>
/// The real <see cref="ISessionActions"/> of the main window: final summaries (F4), speakers
/// (F5) and speaker correction. <see cref="ISessionActions.PlayAt"/> is handled by the
/// window's <see cref="PlaybackSessionActions"/> wrapper, so it does nothing here.
/// </summary>
internal sealed class AppSessionActions(SessionAiServices services) : ISessionActions
{
    private readonly SessionAiServices _services = services ?? throw new ArgumentNullException(nameof(services));

    public bool IsAiAvailable => _services.IsLanguageModelInstalled() || _services.AreSpeakersAvailable();

    /// <summary>Settings default template first, then the rest of the catalog. Never empty.</summary>
    public IReadOnlyList<string> SummaryTemplates
    {
        get
        {
            var preferred = _services.Settings().Summaries.Template;
            List<string> names;
            try
            {
                names = _services.Templates().Entries
                    .Select(entry => entry.Template.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name) &&
                        !string.Equals(name, CatchUpSummarizer.TemplateName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning($"quill: templates unavailable: {exception.Message}");
                names = [];
            }

            var index = names.FindIndex(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase));
            if (index > 0)
            {
                var name = names[index];
                names.RemoveAt(index);
                names.Insert(0, name);
            }

            return names.Count > 0 ? names : [AiPackMissingSessionActions.DefaultTemplate];
        }
    }

    public bool CanReassignSpeakers => true;

    /// <summary>Speaker naming memory and in-person mode (F11/F13); set by the composition root.</summary>
    public IVoiceActions? Voices { get; init; }

    public async Task GenerateSummaryAsync(string sessionId, string template, SummaryLevel level, CancellationToken cancellationToken)
    {
        if (!_services.IsLanguageModelInstalled())
        {
            throw new AiPackMissingException();
        }

        var options = FinalSummaryOptions(_services, template) with { Level = level };
        var outcome = await _services.BuildSummary(sessionId, options, cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case SummaryStatus.Completed:
                await ExportQuietlyAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return;
            case SummaryStatus.AiPackMissing:
                throw new AiPackMissingException();
            case SummaryStatus.NoTranscript:
                throw new InvalidOperationException(Resources.Loc.Get("Pipeline_NoTranscript"));
            default:
                throw new InvalidOperationException(outcome.Error ?? Resources.Loc.Get("Pipeline_SummaryFailed"));
        }
    }

    public async Task IdentifySpeakersAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = await _services.ResolveSpeakers(sessionId, cancellationToken).ConfigureAwait(false);
        switch (result.State)
        {
            case SpeakersResolveState.Resolved:
                return;
            case SpeakersResolveState.Unavailable:
                throw new AiPackMissingException();
            case SpeakersResolveState.NoTranscript:
                throw new InvalidOperationException(Resources.Loc.Get("Pipeline_NoTranscript"));
            default:
                throw new InvalidOperationException(Resources.Loc.Get("Pipeline_SessionNotFound"));
        }
    }

    public async Task ReassignSpeakerAsync(
        string sessionId,
        int segmentIndex,
        TimeSpan segmentStart,
        string expectedSpeakerId,
        string expectedText,
        string speakerId,
        CancellationToken cancellationToken)
    {
        var transcript = await _services.LoadFinalTranscript(sessionId, cancellationToken).ConfigureAwait(false);
        var index = TranscriptSegmentLocator.IndexOf(transcript, segmentIndex, segmentStart, expectedSpeakerId, expectedText);
        if (index < 0)
        {
            throw new InvalidOperationException(Resources.Loc.Get("Pipeline_SegmentNotFound"));
        }

        await _services.ReassignSegment(sessionId, index, speakerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsSummaryOutdatedAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_services.IsSummaryOutdated is not { } check)
        {
            return false;
        }

        try
        {
            return await check(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Playback is routed by <see cref="PlaybackSessionActions"/>; never called.</summary>
    public void PlayAt(string sessionId, TimeSpan time)
    {
    }

    /// <summary>Final summary options from the settings, for <paramref name="template"/> (null: the default).</summary>
    internal static SummaryOptions FinalSummaryOptions(SessionAiServices services, string? template)
    {
        var settings = services.Settings();
        Template? resolved;
        try
        {
            resolved = services.Templates().Resolve(template ?? settings.Summaries.Template);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: templates unavailable, using the default: {exception.Message}");
            resolved = DefaultTemplates.Default;
        }

        return settings.Summaries.ToOptions(resolved);
    }

    private async Task ExportQuietlyAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _services.AutoExport(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // The summary is saved; a failed export must not turn it into an error.
            Trace.TraceWarning($"quill: auto-export of {sessionId} failed: {exception.Message}");
        }
    }
}
