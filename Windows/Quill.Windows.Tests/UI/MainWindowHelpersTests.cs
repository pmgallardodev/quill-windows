using System.Globalization;
using Quill.Core.Domain;
using Quill.Windows.UI;
using Xunit;

namespace Quill.Windows.Tests.UI;

public class MeetingGroupingTests
{
    // Wednesday 2026-11-25, 10:00 at +01:00.
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    [Theory]
    [InlineData(2026, 11, 25, 0, 30, "Today")]
    [InlineData(2026, 11, 25, 23, 0, "Today")]
    [InlineData(2026, 11, 24, 23, 59, "Yesterday")]
    [InlineData(2026, 11, 23, 9, 0, "ThisWeek")]
    [InlineData(2026, 11, 22, 18, 0, "Earlier")]
    public void Classify_BucketsByCalendarDay_WithMondayAsWeekStart(
        int year, int month, int day, int hour, int minute, string expected)
    {
        var started = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(1));

        Assert.Equal(Enum.Parse<MeetingDayBucket>(expected), MeetingGrouping.Classify(started, Now).Bucket);
    }

    [Fact]
    public void Classify_UsesTheViewersOffset_NotTheRecordedOne()
    {
        // 23:30 UTC on the 24th is 00:30 on the 25th at +01:00.
        var started = new DateTimeOffset(2026, 11, 24, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(MeetingDayBucket.Today, MeetingGrouping.Classify(started, Now).Bucket);
    }

    [Fact]
    public void Classify_TreatsFutureStartsAsToday()
    {
        Assert.Equal(MeetingDayBucket.Today, MeetingGrouping.Classify(Now.AddDays(2), Now).Bucket);
    }

    [Fact]
    public void Classify_OnMonday_YesterdayWinsOverLastWeek()
    {
        var monday = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal(MeetingDayBucket.Yesterday, MeetingGrouping.Classify(monday.AddDays(-1), monday).Bucket);
        Assert.Equal(MeetingDayBucket.Earlier, MeetingGrouping.Classify(monday.AddDays(-2), monday).Bucket);
    }

    [Fact]
    public void Label_NamesRelativeBuckets()
    {
        Assert.Equal("Hoy", MeetingGrouping.LabelFor(Now, Now, Spanish));
        Assert.Equal("Ayer", MeetingGrouping.LabelFor(Now.AddDays(-1), Now, Spanish));
        Assert.Equal("Esta semana", MeetingGrouping.LabelFor(Now.AddDays(-2), Now, Spanish));
    }

    [Fact]
    public void Label_SpellsOutOlderDates_AddingTheYearOnlyWhenItDiffers()
    {
        Assert.Equal("Viernes, 20 de noviembre", MeetingGrouping.LabelFor(Now.AddDays(-5), Now, Spanish));
        Assert.Equal("3 de marzo de 2025", MeetingGrouping.LabelFor(new DateTimeOffset(2025, 3, 3, 9, 0, 0, Now.Offset), Now, Spanish));
    }
}

public class SpeakerLabelsTests
{
    [Fact]
    public void Label_PrefersTheNameFromSpeakersJson()
    {
        var labels = new SpeakerLabels(new[] { new Speaker("speaker-2", Track.System, " Ana ") });

        Assert.Equal("Ana", labels.Label("speaker-2"));
        Assert.Equal("Interlocutor 1", labels.Label("speaker-1"));
    }

    [Theory]
    [InlineData("me", "Yo")]
    [InlineData("them", "Otros")]
    [InlineData("speaker-3", "Interlocutor 3")]
    [InlineData(null, "Desconocido")]
    [InlineData("  ", "Desconocido")]
    [InlineData("custom", "custom")]
    public void DefaultLabel_FallsBackToLegacyAndNumberedNames(string? id, string expected)
    {
        Assert.Equal(expected, SpeakerLabels.DefaultLabel(id));
    }
}

public class TextHighlightTests
{
    [Fact]
    public void Split_MarksEveryMatch_IgnoringCaseAndAccents()
    {
        var runs = TextHighlight.Split("La Sesión y otra sesion", "sesion");

        Assert.Equal(
            new[]
            {
                new HighlightRun("La ", false),
                new HighlightRun("Sesión", true),
                new HighlightRun(" y otra ", false),
                new HighlightRun("sesion", true),
            },
            runs);
    }

    [Fact]
    public void Split_WithoutQuery_ReturnsTheWholeText()
    {
        Assert.Equal(new[] { new HighlightRun("hola", false) }, TextHighlight.Split("hola", "  "));
        Assert.Empty(TextHighlight.Split(string.Empty, "x"));
    }

    [Fact]
    public void Split_RunsConcatenateBackToTheOriginal()
    {
        const string text = "abcABCabc";

        Assert.Equal(text, string.Concat(TextHighlight.Split(text, "bc").Select(run => run.Text)));
    }

    [Fact]
    public void Contains_FollowsTheSameRules()
    {
        Assert.True(TextHighlight.Contains("Revisión de código", "REVISION"));
        Assert.False(TextHighlight.Contains("hola", null));
    }
}

public class ActionItemRulesTests
{
    private static readonly MeetingSummary Summary = new()
    {
        ActionItems = new[]
        {
            new ActionItem("Enviar acta", "me", null, IsMine: false, TimeSpan.FromSeconds(10)),
            new ActionItem("Revisar presupuesto", "speaker-1", "viernes", IsMine: false, TimeSpan.FromSeconds(20)),
            new ActionItem("Llamar al proveedor", null, null, IsMine: true, TimeSpan.FromSeconds(30)),
        },
        Meta = new SummaryMeta { Kind = SummaryKind.Live, Model = "m" },
    };

    [Fact]
    public void MineIndexes_IncludesOwnerMeAndIsMine()
    {
        Assert.Equal(new[] { 0, 2 }, ActionItemRules.MineIndexes(Summary));
    }

    [Fact]
    public void SetDone_ChangesOnlyThatItem_AndKeepsMeta()
    {
        var updated = ActionItemRules.SetDone(Summary, 1, done: true);

        Assert.True(updated.ActionItems[1].Done);
        Assert.False(updated.ActionItems[0].Done);
        Assert.False(Summary.ActionItems[1].Done);
        Assert.Equal(SummaryKind.Live, updated.Meta.Kind);
    }

    [Fact]
    public void SetDone_RejectsUnknownIndexes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionItemRules.SetDone(Summary, 3, done: true));
    }
}

public class SummaryFormatterTests
{
    private static readonly MeetingSummary Summary = new()
    {
        ExecutiveSummary = "Se aprobó el plan.",
        Decisions = new[] { new Decision("Lanzar en enero", "speaker-1", TimeSpan.FromSeconds(75)) },
        ActionItems = new[]
        {
            new ActionItem("Enviar acta", "me", "viernes", IsMine: true, TimeSpan.FromMinutes(61), Done: true),
        },
        NextSteps = new[] { "Revisar en dos semanas" },
    };

    private static string Label(string? id) => SpeakerLabels.DefaultLabel(id);

    [Fact]
    public void ToMarkdown_WritesEverySectionInDesignOrder_WithNoneForEmptyOnes()
    {
        var markdown = SummaryFormatter.ToMarkdown(Summary, "Kickoff", Label);

        Assert.StartsWith("# Kickoff", markdown);
        Assert.Contains("- Lanzar en enero — Interlocutor 1 [00:01:15]", markdown);
        Assert.Contains("- [x] Enviar acta — Yo · vence viernes [01:01:00]", markdown);
        Assert.Contains("## Riesgos y bloqueos" + Environment.NewLine + Environment.NewLine + "Ninguno", markdown);

        var order = new[]
        {
            "Resumen ejecutivo", "Decisiones", "Tareas", "Preguntas abiertas", "Temas",
            "Riesgos y bloqueos", "Datos clave", "Próximos pasos",
        }.Select(title => markdown.IndexOf("## " + title, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(index => index), order);
    }

    [Fact]
    public void ToEmail_StartsWithSubject_AndSkipsEmptySections()
    {
        var email = SummaryFormatter.ToEmail(Summary, "Kickoff", Label);

        Assert.StartsWith("Resumen: Kickoff", email);
        Assert.Contains("• Enviar acta — Yo · vence viernes (hecho)", email);
        Assert.DoesNotContain("Riesgos", email);
        Assert.DoesNotContain("[00:", email);
    }

    [Theory]
    [InlineData(0, "[00:00:00]")]
    [InlineData(3_725, "[01:02:05]")]
    public void Cite_UsesTheCitationClock(int seconds, string expected)
    {
        Assert.Equal(expected, SummaryFormatter.Cite(TimeSpan.FromSeconds(seconds)));
    }
}
