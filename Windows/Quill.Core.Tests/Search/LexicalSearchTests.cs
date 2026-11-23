using Xunit;
using Quill.Core.Search;

namespace Quill.Core.Tests.Search;

public sealed class LexicalSearchTests
{
    [Fact]
    public void Folding_removes_accents_and_case_and_drops_stopwords()
    {
        Assert.Equal("reunion sobre camion", TextFolding.Fold("Reunión sobre CAMIÓN"));
        Assert.Equal(["reunion", "presupuesto", "2027"], TextFolding.Tokens("¿La reunión del presupuesto de 2027?"));
    }

    [Fact]
    public void Bm25_ranks_the_passage_with_the_rare_term_first_and_ignores_timestamps()
    {
        var index = new Bm25Index(
        [
            ("a", "[00:00:10] me: hablamos del presupuesto y del calendario"),
            ("b", "[00:01:10] speaker-1: el calendario del proyecto, el calendario de entregas"),
            ("c", "[00:02:10] me: nada que ver"),
        ]);

        var hits = index.Search("presupuesto", 10);
        Assert.Equal("a", Assert.Single(hits).Id);

        var calendar = index.Search("Calendário", 10);
        Assert.Equal(["b", "a"], calendar.Select(hit => hit.Id));

        Assert.Empty(index.Search("00", 10));
    }

    [Fact]
    public void Bm25_respects_the_filter_and_top_k()
    {
        var index = new Bm25Index([("a", "presupuesto"), ("b", "presupuesto anual"), ("c", "presupuesto final")]);

        Assert.Equal(2, index.Search("presupuesto", 2).Count);
        Assert.Equal("b", Assert.Single(index.Search("presupuesto", 10, id => id == "b")).Id);
    }

    [Fact]
    public void Snippet_centers_on_the_first_match_without_timestamps()
    {
        var text = "[00:00:01] me: " + string.Join(' ', Enumerable.Repeat("relleno", 60)) + " el presupuesto final " +
            string.Join(' ', Enumerable.Repeat("cola", 60));

        var snippet = SearchSnippet.Make(text, "presupuesto", 80);

        Assert.Contains("presupuesto", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("[00:00:01]", snippet, StringComparison.Ordinal);
        Assert.True(snippet.Length <= 82, snippet);
    }
}
