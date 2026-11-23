using Quill.Core.Domain;

namespace Quill.Windows.UI;

/// <summary>
/// What the main window asks of the AI and playback modules. The window only depends on
/// this boundary; the composition root wires the real implementations (summaries F4,
/// speakers F5, playback F8). Until then <see cref="AiPackMissingSessionActions"/> keeps
/// the window fully usable and shows the "install the AI pack" states.
/// </summary>
internal interface ISessionActions
{
    /// <summary>False when the AI pack (or the component a feature needs) is not installed.</summary>
    bool IsAiAvailable { get; }

    /// <summary>Template names offered by the template selector, default first. Never empty.</summary>
    IReadOnlyList<string> SummaryTemplates { get; }

    /// <summary>
    /// Generates the final summary and saves it (summary.json) through the session store.
    /// Completes when the file is written; the window then reloads it. Throws on failure.
    /// </summary>
    Task GenerateSummaryAsync(string sessionId, string template, SummaryLevel level, CancellationToken cancellationToken);

    /// <summary>Runs speaker separation and saves speakers.json + the relabelled transcript. Throws on failure.</summary>
    Task IdentifySpeakersAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Request to play the session audio from <paramref name="time"/> (session clock). Fire and forget.</summary>
    void PlayAt(string sessionId, TimeSpan time);

    /// <summary>
    /// True when <see cref="ReassignSpeakerAsync"/> works (speaker correction wired, F5).
    /// The transcript only offers "Cambiar interlocutor" when it is.
    /// </summary>
    bool CanReassignSpeakers => false;

    /// <summary>
    /// Moves the transcript segment that starts at <paramref name="segmentStart"/> to
    /// <paramref name="speakerId"/> and saves the transcript (and speaker corrections).
    /// Completes when saved; the window then reloads. Throws on failure.
    /// Default: not available (<see cref="NotSupportedException"/>).
    /// </summary>
    Task ReassignSpeakerAsync(string sessionId, TimeSpan segmentStart, string speakerId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Speaker reassignment is not available."));
}

/// <summary>Raised by actions that need the AI pack when it is not installed.</summary>
internal sealed class AiPackMissingException : InvalidOperationException
{
    public AiPackMissingException()
        : base("The AI pack is not installed.")
    {
    }
}

/// <summary>
/// Default <see cref="ISessionActions"/>: no AI pack, no playback. AI actions fail with
/// <see cref="AiPackMissingException"/> (the UI never offers them while
/// <see cref="IsAiAvailable"/> is false) and <see cref="PlayAt"/> does nothing.
/// </summary>
internal sealed class AiPackMissingSessionActions : ISessionActions
{
    public const string DefaultTemplate = "general";

    public static AiPackMissingSessionActions Instance { get; } = new();

    public bool IsAiAvailable => false;

    public IReadOnlyList<string> SummaryTemplates { get; } = new[] { DefaultTemplate };

    public Task GenerateSummaryAsync(string sessionId, string template, SummaryLevel level, CancellationToken cancellationToken) =>
        Task.FromException(new AiPackMissingException());

    public Task IdentifySpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromException(new AiPackMissingException());

    public void PlayAt(string sessionId, TimeSpan time)
    {
    }
}
