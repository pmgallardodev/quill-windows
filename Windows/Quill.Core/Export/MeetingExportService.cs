using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Quill.Core.Storage;

namespace Quill.Core.Export;

/// <summary>
/// Exports meetings from the session store to the Markdown folder in <see cref="ExportSettings"/>.
/// Uses the final summary (else the live one) and the final transcript (else the live one).
/// </summary>
public sealed class MeetingExportService
{
    private readonly ISessionStore _store;
    private readonly MarkdownExportLabels _labels;

    public MeetingExportService(ISessionStore store, MarkdownExportLabels? labels = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _labels = labels ?? MarkdownExportLabels.Spanish;
    }

    /// <exception cref="InvalidOperationException">No export folder configured, or no such session.</exception>
    public async Task<MarkdownExportResult> ExportAsync(string sessionId, ExportSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("the export folder is not configured");
        }

        var session = await _store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"session '{sessionId}' does not exist");
        var summary = await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(false)
            ?? await _store.LoadSummaryAsync(sessionId, SummaryKind.Live, cancellationToken).ConfigureAwait(false);
        var transcript = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
        if (transcript.Count == 0)
        {
            transcript = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Live, cancellationToken).ConfigureAwait(false);
        }

        var speakers = await _store.LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = NotesDocument.Parse(await _store.LoadNotesAsync(sessionId, cancellationToken).ConfigureAwait(false));

        var markdown = MarkdownMeetingExporter.Render(new MeetingExport(session, summary, speakers, notes, transcript), _labels);
        var writer = new MarkdownExportWriter(Environment.ExpandEnvironmentVariables(settings.Folder!));
        return writer.Write(session.Id, MarkdownMeetingExporter.FileName(session, _labels), markdown, settings.OverwriteEdited);
    }

    /// <summary>Call after a final summary is saved. Null when auto-export is off or no folder is set.</summary>
    public Task<MarkdownExportResult?> ExportIfEnabledAsync(string sessionId, ExportSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AutoExport && settings.IsConfigured
            ? ExportAsync(sessionId, settings, cancellationToken)!
            : Task.FromResult<MarkdownExportResult?>(null);
    }

    /// <summary>Exports every session. One failing meeting is reported, never fatal for the rest.</summary>
    /// <param name="progress">Number of sessions processed so far.</param>
    public async Task<IReadOnlyList<MarkdownExportResult>> ExportAllAsync(
        ExportSettings settings,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("the export folder is not configured");
        }

        var sessions = await _store.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<MarkdownExportResult>(sessions.Count);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ExportAsync(session.Id, settings, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new MarkdownExportResult(session.Id, MarkdownExportStatus.Failed, null, exception.Message));
            }

            progress?.Report(results.Count);
        }

        return results;
    }
}
