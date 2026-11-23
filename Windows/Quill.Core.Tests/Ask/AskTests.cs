using Xunit;
using Quill.Core.Ask;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Search;
using Quill.Core.Tests.Search;
using Quill.Core.Tests.Summaries;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Ask;

public sealed class AskTests
{
    private readonly FakeSessionStore _store = new();
    private readonly FakeScheduler _scheduler = new();

    private sealed class Collect : IProgress<string>
    {
        public List<string> Pieces { get; } = [];

        public void Report(string value) => Pieces.Add(value);
    }

    private static FakeLanguageModel Answering(string answer, int contextTokens = 8192) =>
        new(contextTokens) { Respond = (_, _) => answer };

    [Fact]
    public async Task Meeting_ask_sends_the_whole_transcript_when_it_fits_and_keeps_only_real_citations()
    {
        _store.Transcripts[("s1", TranscriptPass.Final)] =
        [
            Line(10, 14, "Hola a todos"),
            Line(65, 70, "Aprobamos el presupuesto de 2027", "speaker-1"),
        ];
        var model = Answering("Se aprobó el presupuesto [s1@00:01:05]. También se despidió a Juan [s1@00:09:00].");
        var tokens = new Collect();

        var answer = await new MeetingAsk(_store, model, _scheduler).AskAsync("s1", "¿Qué se aprobó?", tokens, CancellationToken.None);

        var request = Assert.Single(model.JsonCalls).Request;
        Assert.Contains("[s1@00:00:10] me: Hola a todos", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("[s1@00:01:05] speaker-1: Aprobamos el presupuesto de 2027", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("¿Qué se aprobó?", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("español", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(AskPrompts.NotFoundText("es"), request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("[sesión@hh:mm:ss]", request.SystemPrompt, StringComparison.Ordinal);

        var citation = Assert.Single(answer.Citations);
        Assert.Equal(new AskCitation("s1", S(65), "speaker-1: Aprobamos el presupuesto de 2027"), citation);
        Assert.DoesNotContain("00:09:00", answer.Text, StringComparison.Ordinal);
        Assert.Contains("[s1@00:01:05]", answer.Text, StringComparison.Ordinal);
        Assert.Equal("Se aprobó el presupuesto. También se despidió a Juan.", answer.PlainText);
        Assert.False(answer.NotFound);
        Assert.True(answer.IsGrounded);
        Assert.Equal(InferencePriority.Interactive, Assert.Single(_scheduler.Ran));
        Assert.NotEmpty(tokens.Pieces);
    }

    [Fact]
    public async Task Meeting_ask_retrieves_relevant_passages_when_the_transcript_is_too_long()
    {
        var lines = Enumerable.Range(0, 300)
            .Select(index => Line(index * 20, (index * 20) + 15, $"relleno número {index} sobre asuntos varios de la oficina"))
            .ToList();
        lines[150] = Line(3000, 3015, "El presupuesto de marketing sube un diez por ciento", "speaker-2");
        _store.Transcripts[("s1", TranscriptPass.Final)] = lines;
        var model = Answering("Sube un diez por ciento [s1@00:50:00].", contextTokens: 2048);

        var answer = await new MeetingAsk(_store, model, _scheduler).AskAsync("s1", "¿Cuánto sube el presupuesto?", null, CancellationToken.None);

        var prompt = Assert.Single(model.JsonCalls).Request.UserPrompt;
        Assert.Contains("[s1@00:50:00] speaker-2: El presupuesto de marketing", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("relleno número 299 ", prompt, StringComparison.Ordinal);
        Assert.True(TranscriptTokens(prompt) < 2048);
        Assert.Equal(S(3000), Assert.Single(answer.Citations).At);
    }

    [Fact]
    public async Task Citations_without_a_session_refer_to_the_open_meeting_and_tolerate_one_second()
    {
        _store.Transcripts[("s1", TranscriptPass.Final)] = [Line(65.4, 70, "Aprobamos el presupuesto")];
        var model = Answering("Se aprobó [00:01:06].");

        var answer = await new MeetingAsk(_store, model, _scheduler).AskAsync("s1", "¿Qué se aprobó?", null, CancellationToken.None);

        var citation = Assert.Single(answer.Citations);
        Assert.Equal("s1", citation.SessionId);
        Assert.Equal(S(65), citation.At);
    }

    [Fact]
    public async Task Not_found_answers_are_flagged_and_carry_no_citations()
    {
        _store.Transcripts[("s1", TranscriptPass.Final)] = [Line(0, 5, "Hola")];
        var model = Answering("No lo encuentro en tus reuniones.");

        var answer = await new MeetingAsk(_store, model, _scheduler).AskAsync("s1", "¿Quién ganó la liga?", null, CancellationToken.None);

        Assert.True(answer.NotFound);
        Assert.Empty(answer.Citations);
    }

    [Fact]
    public async Task A_meeting_without_transcript_answers_not_found_without_calling_the_model()
    {
        var model = Answering("inventado");

        var answer = await new MeetingAsk(_store, model, _scheduler).AskAsync("s1", "¿Algo?", null, CancellationToken.None);

        Assert.True(answer.NotFound);
        Assert.Equal(AskPrompts.NotFoundText("es"), answer.Text);
        Assert.Empty(model.JsonCalls);
    }

    [Fact]
    public async Task An_answer_without_any_verified_citation_is_marked_ungrounded()
    {
        _store.Transcripts[("s1", TranscriptPass.Final)] = [Line(0, 5, "Hola")];
        var model = Answering("Se habló de muchas cosas.");

        var answer = await new MeetingAsk(_store, model, _scheduler).AskAsync("s1", "¿De qué se habló?", null, CancellationToken.None);

        Assert.False(answer.NotFound);
        Assert.False(answer.IsGrounded);
    }

    [Fact]
    public async Task The_configured_language_is_requested()
    {
        _store.Transcripts[("s1", TranscriptPass.Final)] = [Line(0, 5, "Hola")];
        var model = Answering("x");

        await new MeetingAsk(_store, model, _scheduler, new AskOptions { Language = "en" }).AskAsync("s1", "What?", null, CancellationToken.None);

        var system = Assert.Single(model.JsonCalls).Request.SystemPrompt;
        Assert.Contains("inglés", system, StringComparison.Ordinal);
        Assert.Contains(AskPrompts.NotFoundText("en"), system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Library_ask_retrieves_across_meetings_and_drops_citations_of_other_meetings()
    {
        _store.Transcripts[("lunes", TranscriptPass.Final)] = [Line(0, 5, "El camión llega el jueves")];
        _store.Transcripts[("viernes", TranscriptPass.Final)] = [Line(30, 35, "El camión se retrasa una semana", "speaker-1")];
        _store.Transcripts[("otra", TranscriptPass.Final)] = [Line(0, 5, "Nada relevante aquí")];
        var catalog = new SearchCatalog();
        await new MeetingIndexer(_store, catalog, null, null).IndexAsync(
            [SearchData.Session("lunes", "Logística lunes"), SearchData.Session("viernes", "Logística viernes"), SearchData.Session("otra")],
            CancellationToken.None);
        var model = Answering("Llegaba el jueves [lunes@00:00:00] pero se retrasa [viernes@00:00:30] y [otra@00:00:00].");

        var answer = await new LibraryAsk(new HybridSearch(catalog, null, null), model, _scheduler)
            .AskAsync("¿Cuándo llega el camión?", null, CancellationToken.None);

        var prompt = Assert.Single(model.JsonCalls).Request.UserPrompt;
        Assert.Contains("Logística lunes", prompt, StringComparison.Ordinal);
        Assert.Contains("[viernes@00:00:30] speaker-1: El camión se retrasa", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Nada relevante", prompt, StringComparison.Ordinal);
        Assert.Equal(["lunes", "viernes"], answer.Citations.Select(citation => citation.SessionId));
        Assert.Equal(2, answer.Sources.Count);
        Assert.Equal(InferencePriority.Interactive, Assert.Single(_scheduler.Ran));
    }

    [Fact]
    public async Task Library_ask_with_no_matching_passage_answers_not_found_without_calling_the_model()
    {
        _store.Transcripts[("lunes", TranscriptPass.Final)] = [Line(0, 5, "El camión llega el jueves")];
        var catalog = new SearchCatalog();
        await new MeetingIndexer(_store, catalog, null, null).IndexAsync([SearchData.Session("lunes")], CancellationToken.None);
        var model = Answering("inventado");

        var answer = await new LibraryAsk(new HybridSearch(catalog, null, null), model, _scheduler)
            .AskAsync("¿Quién ganó la liga?", null, CancellationToken.None);

        Assert.True(answer.NotFound);
        Assert.Empty(model.JsonCalls);
    }

    private static int TranscriptTokens(string prompt) => Quill.Core.Transcripts.TranscriptChunker.EstimateTokens(prompt);
}
