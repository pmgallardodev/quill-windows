using Quill.Core.Domain;
using Quill.Core.Storage;
using Quill.Core.Summaries;
using Quill.Windows.Resources;
using Quill.Windows.UI.Theme.Controls;

namespace Quill.Windows.UI.Companion;

/// <summary>Status line above the live summary sections.</summary>
internal enum LiveSummaryBanner
{
    /// <summary>A summary is shown and nothing is pending worth mentioning.</summary>
    None,

    /// <summary>No summary yet: waiting for enough speech.</summary>
    Waiting,

    /// <summary>A refresh is running.</summary>
    Updating,

    /// <summary>The scheduler was busy; the refresh is retried later (design §7.2).</summary>
    Delayed,

    /// <summary>The last refresh failed; the next one waits for more speech.</summary>
    Failed,

    /// <summary>No language model: "Instala el paquete de IA".</summary>
    AiPackMissing,

    /// <summary>The AI pack is there but the live summary is not running (e.g. no GPU).</summary>
    Off,
}

internal enum SummaryItemKind
{
    Decision,
    ActionItem,
    OpenQuestion,
}

/// <param name="Detail">Secondary line (who decided, owner and due date), or null.</param>
/// <param name="At">Transcript offset the item cites.</param>
internal sealed record SummaryItem(SummaryItemKind Kind, string Text, string? Detail, TimeSpan At)
{
    public string AtLabel => TimestampLink.Format(At);

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>The live sections of a <see cref="MeetingSummary"/>, ready to bind.</summary>
internal sealed record CompanionSummaryView(
    string ExecutiveSummary,
    IReadOnlyList<SummaryItem> Decisions,
    IReadOnlyList<SummaryItem> ActionItems,
    IReadOnlyList<SummaryItem> OpenQuestions)
{
    public static CompanionSummaryView Empty { get; } = new(string.Empty, [], [], []);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ExecutiveSummary) && Decisions.Count == 0 && ActionItems.Count == 0 && OpenQuestions.Count == 0;
}

/// <summary>State of the "¿Qué me perdí?" card.</summary>
internal enum CatchUpState
{
    Hidden,
    Loading,
    Ready,

    /// <summary>Nothing was said in the window, or the recap came back empty.</summary>
    Empty,
    AiPackMissing,
    Failed,
}

/// <summary>
/// Pure mapping from Core results to what the panel shows. No WPF types, so it is unit tested
/// directly.
/// </summary>
internal static class CompanionPresenter
{
    public static LiveSummaryBanner Banner(bool hasFeed, bool isAiPackInstalled, LiveSummaryState state, bool hasSummary)
    {
        if (!hasFeed)
        {
            return isAiPackInstalled ? LiveSummaryBanner.Off : LiveSummaryBanner.AiPackMissing;
        }

        return state switch
        {
            LiveSummaryState.AiPackMissing => LiveSummaryBanner.AiPackMissing,
            LiveSummaryState.Delayed => LiveSummaryBanner.Delayed,
            LiveSummaryState.Failed => LiveSummaryBanner.Failed,
            LiveSummaryState.Updating => LiveSummaryBanner.Updating,
            _ => hasSummary ? LiveSummaryBanner.None : LiveSummaryBanner.Waiting,
        };
    }

    /// <summary>Strings.resx key for a banner, or null when nothing is shown.</summary>
    public static string? BannerKey(LiveSummaryBanner banner) => banner switch
    {
        LiveSummaryBanner.Waiting => "Companion_Summary_Waiting",
        LiveSummaryBanner.Updating => "Companion_Summary_Updating",
        LiveSummaryBanner.Delayed => "Companion_Summary_Delayed",
        LiveSummaryBanner.Failed => "Companion_Summary_Failed",
        LiveSummaryBanner.AiPackMissing => "Companion_Summary_AiPackMissing",
        LiveSummaryBanner.Off => "Companion_Summary_Off",
        _ => null,
    };

    public static CompanionSummaryView View(MeetingSummary? summary)
    {
        if (summary is null)
        {
            return CompanionSummaryView.Empty;
        }

        return new CompanionSummaryView(
            summary.ExecutiveSummary?.Trim() ?? string.Empty,
            summary.Decisions
                .Select(decision => new SummaryItem(SummaryItemKind.Decision, decision.Text, SpeakerLabelOrNull(decision.DecidedBy), decision.At))
                .ToArray(),
            summary.ActionItems
                .Select(item => new SummaryItem(SummaryItemKind.ActionItem, item.Text, ActionDetail(item), item.At))
                .ToArray(),
            summary.OpenQuestions
                .Select(question => new SummaryItem(SummaryItemKind.OpenQuestion, question.Text, null, question.At))
                .ToArray());
    }

    public static CatchUpState CatchUpStateOf(SummaryOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Status switch
        {
            SummaryStatus.Completed => View(outcome.Summary).IsEmpty ? CatchUpState.Empty : CatchUpState.Ready,
            SummaryStatus.NoTranscript => CatchUpState.Empty,
            SummaryStatus.AiPackMissing => CatchUpState.AiPackMissing,
            _ => CatchUpState.Failed,
        };
    }

    /// <summary>"Yo", "Interlocutor N", or the id itself (a renamed speaker or a free-text owner).</summary>
    public static string SpeakerLabel(string speakerId)
    {
        ArgumentNullException.ThrowIfNull(speakerId);
        if (SpeakerChip.IsMeId(speakerId))
        {
            return Loc.Get("Speaker_Me");
        }

        return SpeakerIds.IsRemote(speakerId) && SpeakerChip.TrailingNumber(speakerId) is { } number
            ? Loc.Format("Speaker_Numbered", number)
            : speakerId;
    }

    /// <summary>"12:04" or "1:02:03", the timer in the header and the pill.</summary>
    public static string ElapsedLabel(TimeSpan elapsed) =>
        TimestampLink.Format(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);

    /// <summary>
    /// Appends a quick note as "- [hh:mm:ss] text" (notes.md convention, <see cref="NotesDocument"/>).
    /// Returns <paramref name="markdown"/> unchanged when the note is blank.
    /// </summary>
    public static string AppendNote(string? markdown, string? text, TimeSpan at)
    {
        var current = markdown ?? string.Empty;
        var line = NotesDocument.Render([new Note(text ?? string.Empty, at < TimeSpan.Zero ? TimeSpan.Zero : at)]);
        if (line.Length == 0)
        {
            return current;
        }

        return current.Length == 0 || current.EndsWith('\n') ? current + line : current + "\n" + line;
    }

    private static string? SpeakerLabelOrNull(string? speakerId) =>
        string.IsNullOrWhiteSpace(speakerId) ? null : SpeakerLabel(speakerId.Trim());

    private static string? ActionDetail(ActionItem item)
    {
        var owner = SpeakerLabelOrNull(item.Owner);
        var due = string.IsNullOrWhiteSpace(item.Due) ? null : item.Due.Trim();
        return (owner, due) switch
        {
            (null, null) => null,
            (not null, null) => owner,
            (null, not null) => Loc.Format("Companion_Due", due),
            _ => $"{owner} · {Loc.Format("Companion_Due", due)}",
        };
    }
}
