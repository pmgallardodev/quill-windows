using Quill.Core.Domain;
using Quill.Core.Export;
using Xunit;

namespace Quill.Core.Tests.Export;

public sealed class MarkdownMeetingExporterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(1));

    internal static Session Meeting(string? title = "Revisión con \"Acme\"") =>
        new("2026-11-23_10-00-00", "/rec/2026-11-23_10-00-00", title, Start, Start.AddMinutes(45).AddSeconds(12), [], []);

    private static TimeSpan T(int minutes, int seconds) => new(0, minutes, seconds);

    internal static MeetingSummary FullSummary() => new()
    {
        ExecutiveSummary = "Se acordó el plan.\n\nQueda pendiente el presupuesto.",
        Decisions = [new Decision("Usar Postgres", "speaker-1", T(1, 23))],
        ActionItems =
        [
            new ActionItem("Enviar presupuesto", "speaker-1", "viernes", false, T(2, 5)),
            new ActionItem("Revisar contrato", SpeakerIds.Me, null, true, T(3, 0), Done: true),
        ],
        OpenQuestions = [],
        Topics = [new Topic("Arquitectura", T(0, 10))],
        Risks = [new Risk("Fecha ajustada", new TimeSpan(1, 2, 3))],
        KeyFacts = [],
        NextSteps = ["Reunión el lunes"],
        Meta = new SummaryMeta { Template = "client-call", Kind = SummaryKind.Final },
    };

    private static MeetingExport Export(MeetingSummary? summary = null, IReadOnlyList<Speaker>? speakers = null) => new(
        Meeting(),
        summary ?? FullSummary(),
        speakers ?? [new Speaker("speaker-1", Track.System, "Juan")],
        [new Note("Preguntar por el SLA", T(4, 0)), new Note("Nota libre")],
        [
            new TranscriptSegment(T(0, 10), T(0, 15), "Hola a todos.", SpeakerIds.Me, Track.Microphone),
            new TranscriptSegment(T(1, 20), T(1, 30), "Vamos con Postgres.", "speaker-1", Track.System),
            new TranscriptSegment(T(2, 0), T(2, 10), "¿Y el coste?", "speaker-2", Track.System),
        ]);

    [Fact]
    public void WritesObsidianFrontmatter()
    {
        var markdown = MarkdownMeetingExporter.Render(Export());

        Assert.StartsWith(
            "---\n" +
            "title: \"Revisión con \\\"Acme\\\"\"\n" +
            "date: 2026-11-23\n" +
            "time: \"10:00\"\n" +
            "duration: \"00:45:12\"\n" +
            "participants:\n" +
            "  - \"Yo\"\n" +
            "  - \"Juan\"\n" +
            "  - \"Interlocutor 2\"\n" +
            "tags: [quill, meeting]\n" +
            "template: \"client-call\"\n" +
            "quill_session: \"2026-11-23_10-00-00\"\n" +
            "---\n\n" +
            "# Revisión con \"Acme\"\n",
            markdown);
    }

    [Fact]
    public void RendersEverySectionInContractOrderWithNoneForEmptyOnes()
    {
        var markdown = MarkdownMeetingExporter.Render(Export());
        var labels = MarkdownExportLabels.Spanish;
        string[] headings =
        [
            labels.ExecutiveSummary, labels.Decisions, labels.ActionItems, labels.OpenQuestions,
            labels.Topics, labels.Risks, labels.KeyFacts, labels.NextSteps, labels.Notes, labels.Transcript,
        ];

        var positions = headings.Select(heading => markdown.IndexOf("\n## " + heading + "\n", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Contains("## Preguntas abiertas\n\nNinguno\n", markdown);
        Assert.Contains("## Datos clave\n\nNinguno\n", markdown);
        Assert.Contains("Se acordó el plan.\n\nQueda pendiente el presupuesto.\n", markdown);
    }

    [Fact]
    public void RendersItemsWithTimestampsOwnersAndCheckboxes()
    {
        var markdown = MarkdownMeetingExporter.Render(Export());

        Assert.Contains("- [01:23] Usar Postgres (decidido por Juan)\n", markdown);
        Assert.Contains("- [ ] [02:05] Enviar presupuesto (responsable: Juan; fecha: viernes)\n", markdown);
        Assert.Contains("- [x] [03:00] Revisar contrato (responsable: Yo)\n", markdown);
        Assert.Contains("- [00:10] Arquitectura\n", markdown);
        Assert.Contains("- [1:02:03] Fecha ajustada\n", markdown);
        Assert.Contains("- Reunión el lunes\n", markdown);
        Assert.Contains("- [04:00] Preguntar por el SLA\n- Nota libre\n", markdown);
    }

    [Fact]
    public void RendersTheTranscriptWithSpeakerNames()
    {
        var markdown = MarkdownMeetingExporter.Render(Export());

        Assert.Contains(
            "**[00:10] Yo:** Hola a todos.\n\n" +
            "**[01:20] Juan:** Vamos con Postgres.\n\n" +
            "**[02:00] Interlocutor 2:** ¿Y el coste?\n",
            markdown);
        Assert.EndsWith("¿Y el coste?\n", markdown);
    }

    [Fact]
    public void RenamedOwnerAndUnknownIdsUseTheirLabels()
    {
        var summary = FullSummary() with
        {
            Decisions = [new Decision("A", SpeakerIds.LegacyThem, T(0, 1)), new Decision("B", null, T(0, 2))],
        };

        var markdown = MarkdownMeetingExporter.Render(Export(summary, speakers: [new Speaker(SpeakerIds.Me, Track.Microphone, "Ana")]));

        Assert.Contains("- [00:01] A (decidido por Ellos)\n", markdown);
        Assert.Contains("- [00:02] B\n", markdown);
        Assert.Contains("  - \"Ana\"\n", markdown);
    }

    [Fact]
    public void WithoutASummaryItSaysSoAndKeepsNotesAndTranscript()
    {
        var input = Export() with { Summary = null };

        var markdown = MarkdownMeetingExporter.Render(input);

        Assert.Contains("_Sin resumen todavía._", markdown);
        Assert.DoesNotContain("## Decisiones", markdown);
        Assert.DoesNotContain("template:", markdown);
        Assert.Contains("## Notas", markdown);
        Assert.Contains("## Transcripción", markdown);
    }

    [Fact]
    public void EmptyTranscriptAndNotesSayNone()
    {
        var input = Export() with { Notes = [], Transcript = [], Speakers = [] };

        var markdown = MarkdownMeetingExporter.Render(input);

        Assert.Contains("participants: []\n", markdown);
        Assert.Contains("## Notas\n\nNinguno\n", markdown);
        Assert.EndsWith("## Transcripción\n\nNinguno\n", markdown);
    }

    [Theory]
    [InlineData("Revisión con \"Acme\"", "2026-11-23-revision-con-acme.md")]
    [InlineData("  Daily / Sprint #42: ¡ñandú!  ", "2026-11-23-daily-sprint-42-nandu.md")]
    [InlineData(null, "2026-11-23-1000-reunion.md")]
    [InlineData("???", "2026-11-23-1000-reunion.md")]
    public void FileNameIsADatePrefixedSlug(string? title, string expected)
    {
        Assert.Equal(expected, MarkdownMeetingExporter.FileName(Meeting(title)));
    }

    [Fact]
    public void LongTitlesAreCutAtAWordBoundary()
    {
        var name = MarkdownMeetingExporter.FileName(Meeting(string.Join(' ', Enumerable.Repeat("palabra", 30))));

        Assert.True(name.Length <= "2026-11-23-".Length + MarkdownMeetingExporter.MaxSlugLength + ".md".Length);
        Assert.EndsWith("palabra.md", name);
    }
}
