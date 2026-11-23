using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Core.Summaries;

namespace Quill.Windows.UI.Companion;

/// <summary>"¿Qué me perdí?" for the last <paramref name="window"/> of the recording.</summary>
internal delegate Task<SummaryOutcome> CatchUpHandler(TimeSpan window, CancellationToken cancellationToken);

/// <summary>
/// Everything the companion panel needs from the host for one recording. The panel never
/// touches capture itself: pause, stop and markers go through these callbacks, which the panel
/// invokes on the UI thread. Optional members switch their feature to an explanatory state
/// instead of failing (no live text, no AI pack, no level meters).
/// </summary>
internal sealed record CompanionContext
{
    /// <summary>Session folder name, the key for <see cref="Store"/>.</summary>
    public required string SessionId { get; init; }

    /// <summary>Reads and writes notes.md through <see cref="ISessionStore.LoadNotesAsync"/> / <see cref="ISessionStore.SaveNotesAsync"/>.</summary>
    public required ISessionStore Store { get; init; }

    /// <summary>Recorded time without pauses (RecordingSession.Elapsed). Polled on the UI thread.</summary>
    public required Func<TimeSpan> Elapsed { get; init; }

    /// <summary>Whether capture is paused right now. Polled on the UI thread.</summary>
    public required Func<bool> IsPaused { get; init; }

    /// <summary>Pauses or resumes the recording (same action as the tray menu).</summary>
    public required Action TogglePause { get; init; }

    /// <summary>Stops the recording. The host closes the panel afterwards.</summary>
    public required Action Stop { get; init; }

    /// <summary>Marks the current moment (RecordingSession.AddMarker).</summary>
    public required Action AddMarker { get; init; }

    /// <summary>Live transcript, or null when live transcription is unavailable.</summary>
    public LiveTranscriptBuffer? Transcript { get; init; }

    /// <summary>Rolling summary loop, or null when the live summary is off.</summary>
    public LiveSummaryLoop? LiveSummary { get; init; }

    /// <summary>Whether the AI pack provides a language model (SummariesModule.IsLanguageModelInstalled).</summary>
    public bool IsAiPackInstalled { get; init; }

    /// <summary>How far back "¿Qué me perdí?" looks (settings "summaries.catch_up_minutes").</summary>
    public TimeSpan CatchUpWindow { get; init; } = CatchUpSummarizer.DefaultWindow;

    /// <summary>Recap of the last minutes, or null when no language model is available.</summary>
    public CatchUpHandler? CatchUp { get; init; }

    /// <summary>Microphone level 0..1, or null to hide the meter. Polled about 15 times per second.</summary>
    public Func<double>? MicrophoneLevel { get; init; }

    /// <summary>System audio level 0..1, or null to hide the meter.</summary>
    public Func<double>? SystemLevel { get; init; }
}
