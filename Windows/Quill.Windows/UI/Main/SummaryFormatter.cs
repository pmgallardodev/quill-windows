using System.Text;
using Quill.Core.Domain;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>
/// Turns a <see cref="MeetingSummary"/> into text for the clipboard: Markdown (every
/// section, "Ninguno" when empty, citations as [hh:mm:ss]) and a plain email body (only
/// sections with content, no citations). Section order is design §7.1.
/// </summary>
internal static class SummaryFormatter
{
    public static string ToMarkdown(MeetingSummary summary, string title, Func<string?, string> speakerLabel)
    {
        var none = Loc.Get("Common_None");
        var text = new StringBuilder();
        text.Append("# ").AppendLine(title.Trim()).AppendLine();

        Heading(text, "Summary_ExecutiveSummary");
        text.AppendLine(string.IsNullOrWhiteSpace(summary.ExecutiveSummary) ? none : summary.ExecutiveSummary.Trim())
            .AppendLine();

        Section(text, "Summary_Decisions", summary.Decisions, none, decision =>
            $"- {decision.Text.Trim()}{Who(decision.DecidedBy, speakerLabel)} {Cite(decision.At)}");

        Section(text, "Summary_ActionItems", summary.ActionItems, none, item =>
            $"- [{(item.Done ? "x" : " ")}] {item.Text.Trim()}{Who(item.Owner, speakerLabel)}{Due(item.Due)} {Cite(item.At)}");

        Section(text, "Summary_OpenQuestions", summary.OpenQuestions, none, question =>
            $"- {question.Text.Trim()} {Cite(question.At)}");

        Section(text, "Summary_Topics", summary.Topics, none, topic =>
            $"- {topic.Title.Trim()} {Cite(topic.Start)}");

        Section(text, "Summary_Risks", summary.Risks, none, risk =>
            $"- {risk.Text.Trim()} {Cite(risk.At)}");

        Section(text, "Summary_KeyFacts", summary.KeyFacts, none, fact =>
            $"- {fact.Text.Trim()} {Cite(fact.At)}");

        Section(text, "Summary_NextSteps", summary.NextSteps, none, step => $"- {step.Trim()}");

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>First line is the subject; the body skips empty sections.</summary>
    public static string ToEmail(MeetingSummary summary, string title, Func<string?, string> speakerLabel)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.Format("Summary_EmailSubject", title.Trim())).AppendLine();

        if (!string.IsNullOrWhiteSpace(summary.ExecutiveSummary))
        {
            text.AppendLine(summary.ExecutiveSummary.Trim()).AppendLine();
        }

        EmailSection(text, "Summary_Decisions", summary.Decisions, decision =>
            $"{decision.Text.Trim()}{Who(decision.DecidedBy, speakerLabel)}");

        EmailSection(text, "Summary_ActionItems", summary.ActionItems, item =>
            $"{item.Text.Trim()}{Who(item.Owner, speakerLabel)}{Due(item.Due)}" +
            (item.Done ? $" ({Loc.Get("Summary_Done")})" : string.Empty));

        EmailSection(text, "Summary_OpenQuestions", summary.OpenQuestions, question => question.Text.Trim());
        EmailSection(text, "Summary_Topics", summary.Topics, topic => topic.Title.Trim());
        EmailSection(text, "Summary_Risks", summary.Risks, risk => risk.Text.Trim());
        EmailSection(text, "Summary_KeyFacts", summary.KeyFacts, fact => fact.Text.Trim());
        EmailSection(text, "Summary_NextSteps", summary.NextSteps, step => step.Trim());

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>Citation format used everywhere in summaries: [hh:mm:ss].</summary>
    public static string Cite(TimeSpan at) =>
        $"[{(int)at.TotalHours:D2}:{at.Minutes:D2}:{at.Seconds:D2}]";

    private static void Heading(StringBuilder text, string key) =>
        text.Append("## ").AppendLine(Loc.Get(key)).AppendLine();

    private static void Section<T>(
        StringBuilder text,
        string key,
        IReadOnlyList<T> items,
        string none,
        Func<T, string> line)
    {
        Heading(text, key);
        if (items.Count == 0)
        {
            text.AppendLine(none).AppendLine();
            return;
        }

        foreach (var item in items)
        {
            text.AppendLine(line(item));
        }

        text.AppendLine();
    }

    private static void EmailSection<T>(StringBuilder text, string key, IReadOnlyList<T> items, Func<T, string> line)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Append(Loc.Get(key)).AppendLine(":");
        foreach (var item in items)
        {
            text.Append("• ").AppendLine(line(item));
        }

        text.AppendLine();
    }

    private static string Who(string? speakerId, Func<string?, string> speakerLabel) =>
        string.IsNullOrWhiteSpace(speakerId) ? string.Empty : $" — {speakerLabel(speakerId)}";

    private static string Due(string? due) =>
        string.IsNullOrWhiteSpace(due) ? string.Empty : $" · {Loc.Format("Summary_Due", due.Trim())}";
}
