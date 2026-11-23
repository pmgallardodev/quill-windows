using Quill.Core.Ask;
using Quill.Core.Search;
using Quill.Core.Summaries;
using Quill.Windows.Adapters.Embeddings;
using Quill.Windows.Ask;
using Quill.Windows.Tests.UI;
using Quill.Windows.UI;
using Xunit;

namespace Quill.Windows.Tests.Ask;

internal sealed class FakeAskService : IAskService
{
    public bool CanAsk { get; set; } = true;

    public Func<string?, string, CancellationToken, Task<AskAnswer>> OnAsk { get; set; } =
        (_, _, _) => Task.FromResult(AskAnswer.NothingFound("es"));

    public Func<SearchQuery, Task<SearchResponse>> OnSearch { get; set; } =
        _ => Task.FromResult(new SearchResponse([], UsedVectors: true));

    public List<(string SessionId, TimeSpan At)> Opened { get; } = new();

    public List<string?> AskedSessions { get; } = new();

    public Task<AskAnswer> AskMeetingAsync(string sessionId, string question, IProgress<string>? tokens, CancellationToken cancellationToken)
    {
        AskedSessions.Add(sessionId);
        return OnAsk(sessionId, question, cancellationToken);
    }

    public Task<AskAnswer> AskLibraryAsync(string question, IProgress<string>? tokens, CancellationToken cancellationToken)
    {
        AskedSessions.Add(null);
        return OnAsk(null, question, cancellationToken);
    }

    public Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken) => OnSearch(query);

    public void OpenAt(string sessionId, TimeSpan at) => Opened.Add((sessionId, at));
}

public sealed class AskViewModelTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeAskService _service = new();

    public AskViewModelTests() => Samples.UseSpanish();

    private static AskSource Source(string sessionId, string title) =>
        new(new Passage($"{sessionId}:transcript:0", sessionId, PassageKinds.Transcript, TimeSpan.Zero, TimeSpan.FromSeconds(90), ["me"],
            "[00:01:05] me: Aprobamos el presupuesto"), title, Started);

    [Fact]
    public async Task Meeting_answer_shows_plain_text_and_citation_chips_that_play_the_moment()
    {
        var citation = new AskCitation("s1", TimeSpan.FromSeconds(65), "me: Aprobamos el presupuesto");
        _service.OnAsk = (_, _, _) => Task.FromResult(new AskAnswer(
            "Se aprobó [s1@00:01:05].", "Se aprobó.", [citation], false, [Source("s1", "Comité")]));
        var viewModel = new AskViewModel(_service, "s1") { Question = "¿Qué se aprobó?" };

        await viewModel.AskAsync();

        Assert.Equal("Se aprobó.", viewModel.Answer);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsUngrounded);
        var chip = Assert.Single(viewModel.Citations);
        Assert.Equal("00:01:05", chip.Label);
        Assert.Equal("me: Aprobamos el presupuesto", chip.Quote);
        chip.OpenCommand.Execute(null);
        Assert.Equal(("s1", TimeSpan.FromSeconds(65)), Assert.Single(_service.Opened));
        Assert.Equal(["s1"], _service.AskedSessions);
    }

    [Fact]
    public async Task Library_chips_name_the_meeting()
    {
        var citation = new AskCitation("s2", TimeSpan.FromSeconds(65), "me: x");
        _service.OnAsk = (_, _, _) => Task.FromResult(new AskAnswer("a", "a", [citation], false, [Source("s2", "Logística")]));
        var viewModel = new AskViewModel(_service, sessionId: null) { Question = "¿Cuándo?" };

        await viewModel.AskAsync();

        Assert.Equal("Logística · 00:01:05", Assert.Single(viewModel.Citations).Label);
        Assert.Equal([null], _service.AskedSessions);
    }

    [Fact]
    public async Task An_answer_without_citations_is_flagged_as_ungrounded()
    {
        _service.OnAsk = (_, _, _) => Task.FromResult(new AskAnswer("Algo.", "Algo.", [], false, []));
        var viewModel = new AskViewModel(_service, "s1") { Question = "¿Algo?" };

        await viewModel.AskAsync();

        Assert.True(viewModel.IsUngrounded);
        Assert.False(viewModel.HasCitations);
    }

    [Fact]
    public async Task Missing_ai_pack_disables_asking_and_explains_why()
    {
        _service.CanAsk = false;
        var viewModel = new AskViewModel(_service, "s1") { Question = "¿Algo?" };

        await viewModel.AskAsync();

        Assert.Empty(_service.AskedSessions);
        Assert.False(viewModel.AskCommand.CanExecute(null));
        Assert.Contains("paquete de IA", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_fails_to_load_shows_the_ai_pack_message()
    {
        _service.OnAsk = (_, _, _) => Task.FromException<AskAnswer>(new LanguageModelUnavailableException("gone"));
        var viewModel = new AskViewModel(_service, "s1") { Question = "¿Algo?" };

        await viewModel.AskAsync();

        Assert.Contains("paquete de IA", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Search_lists_results_with_snippet_and_opens_the_moment()
    {
        var passage = new Passage("s1:transcript:0", "s1", PassageKinds.Transcript, TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(80), ["me"],
            "[00:01:05] me: Aprobamos el presupuesto");
        _service.OnSearch = query => Task.FromResult(new SearchResponse(
            [new SearchResult(passage, "Comité", Started, 0.03, 1, null)], UsedVectors: false));
        var viewModel = new LibrarySearchViewModel(_service) { Query = "presupuesto" };

        await viewModel.SearchAsync();

        var result = Assert.Single(viewModel.Results);
        Assert.Equal("Comité", result.Title);
        Assert.Equal("00:01:05", result.TimeText);
        Assert.Equal("me: Aprobamos el presupuesto", result.Snippet);
        Assert.Contains("palabras", viewModel.Status, StringComparison.Ordinal);
        result.OpenCommand.Execute(null);
        Assert.Equal(("s1", TimeSpan.FromSeconds(65)), Assert.Single(_service.Opened));
    }

    [Fact]
    public async Task Embedding_model_without_a_file_is_unavailable_instead_of_crashing()
    {
        using var model = new LlamaSharpEmbeddingModel(() => null);

        await Assert.ThrowsAsync<EmbeddingModelUnavailableException>(() => model.EmbedAsync(["hola"], CancellationToken.None));
        Assert.Equal("unavailable", model.ModelName);
    }

    [Fact]
    public void Per_token_embeddings_are_mean_pooled()
    {
        var pooled = LlamaSharpEmbeddingModel.Pool([[1f, 0f], [0f, 1f]], 2);

        Assert.Equal([0.5f, 0.5f], pooled);
    }
}
