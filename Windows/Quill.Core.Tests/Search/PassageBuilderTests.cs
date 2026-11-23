using Xunit;
using Quill.Core.Domain;
using Quill.Core.Search;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Search;

public sealed class PassageBuilderTests
{
    [Fact]
    public void Transcript_windows_stay_within_bounds_and_overlap()
    {
        // 20 lines of 10 s each: 200 s of speech.
        var segments = Enumerable.Range(0, 20)
            .Select(index => Line(index * 10, (index * 10) + 9, $"frase {index}", index % 2 == 0 ? SpeakerIds.Me : "speaker-1"))
            .ToList();

        var passages = PassageBuilder.FromTranscript("s1", segments);

        Assert.True(passages.Count >= 3);
        Assert.All(passages, passage =>
        {
            Assert.Equal(PassageKinds.Transcript, passage.Kind);
            Assert.Equal("s1", passage.SessionId);
            var length = passage.End!.Value - passage.Start!.Value;
            Assert.True(length <= TimeSpan.FromSeconds(90), $"window of {length}");
        });
        Assert.Equal(S(0), passages[0].Start);
        Assert.Equal(S(199), passages[^1].End);

        // Consecutive windows share at least one line (overlap) and always move forward.
        for (var index = 1; index < passages.Count; index++)
        {
            Assert.True(passages[index].Start > passages[index - 1].Start);
            Assert.True(passages[index].Start < passages[index - 1].End);
        }

        Assert.Equal(passages.Count, passages.Select(passage => passage.Id).Distinct().Count());
    }

    [Fact]
    public void Transcript_passage_text_uses_the_chunker_line_format_and_lists_speakers()
    {
        var passages = PassageBuilder.FromTranscript("s1",
        [
            Line(61, 64, "Hola a todos"),
            Line(65, 70, "Empezamos con el presupuesto", "speaker-1"),
        ]);

        var passage = Assert.Single(passages);
        Assert.Equal("[00:01:01] me: Hola a todos\n[00:01:05] speaker-1: Empezamos con el presupuesto", passage.Text);
        Assert.Equal(["me", "speaker-1"], passage.Speakers);
    }

    [Fact]
    public void Blank_segments_are_skipped_and_an_empty_transcript_gives_no_passages()
    {
        Assert.Empty(PassageBuilder.FromTranscript("s1", [Line(0, 1, "  ")]));
        Assert.Empty(PassageBuilder.FromTranscript("s1", []));
    }

    [Fact]
    public void A_single_very_long_segment_becomes_its_own_passage()
    {
        var passages = PassageBuilder.FromTranscript("s1",
        [
            Line(0, 5, "corta"),
            Line(6, 200, "muy larga"),
            Line(201, 205, "otra corta"),
        ]);

        Assert.Contains(passages, passage => passage.Text.Contains("muy larga", StringComparison.Ordinal));
        Assert.Contains(passages, passage => passage.Text.Contains("otra corta", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_items_and_notes_become_cited_passages()
    {
        var summary = new MeetingSummary
        {
            ExecutiveSummary = "Se habló del presupuesto.",
            Decisions = [new Decision("Aprobar el presupuesto", "me", S(65))],
            ActionItems = [new ActionItem("Enviar el informe", "speaker-1", null, false, S(120))],
        };

        var fromSummary = PassageBuilder.FromSummary("s1", summary);
        var fromNotes = PassageBuilder.FromNotes("s1", [new Note("Revisar cifras", S(30)), new Note("Sin hora")]);

        Assert.Contains(fromSummary, passage => passage.Kind == PassageKinds.Summary && passage.Start == S(65) &&
            passage.Text.Contains("Aprobar el presupuesto", StringComparison.Ordinal));
        Assert.Contains(fromSummary, passage => passage.Start == S(120));
        Assert.Contains(fromSummary, passage => passage.Start is null && passage.Text.Contains("presupuesto", StringComparison.Ordinal));
        Assert.Equal(2, fromNotes.Count);
        Assert.Equal("[00:00:30] nota: Revisar cifras", fromNotes[0].Text);
        Assert.Null(fromNotes[1].Start);
    }
}
