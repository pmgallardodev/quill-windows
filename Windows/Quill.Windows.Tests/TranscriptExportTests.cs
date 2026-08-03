using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Windows.Tests;

public class TranscriptExportTests
{
    private static readonly TranscriptSegment[] Two =
    {
        new("me", 0, 4_000, "Buenos dias, empezamos"),
        new("them", 4_500, 9_250, "Perfecto, te escucho"),
    };

    [Fact]
    public void ToSrt_NumbersCuesFromOne_AndUsesCommaForMilliseconds()
    {
        var srt = TranscriptExport.ToSrt(Two);

        Assert.StartsWith("1\n00:00:00,000 --> 00:00:04,000\n", srt.ReplaceLineEndings("\n"));
        Assert.Contains("2\n00:00:04,500 --> 00:00:09,250\n", srt.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ToVtt_StartsWithTheHeader_AndUsesDotForMilliseconds()
    {
        var vtt = TranscriptExport.ToVtt(Two).ReplaceLineEndings("\n");

        Assert.StartsWith("WEBVTT\n", vtt);
        Assert.Contains("00:00:04.500 --> 00:00:09.250", vtt);
        Assert.DoesNotContain("00:00:04,500", vtt);
    }

    [Fact]
    public void BothFormats_KeepTheSpeakerLabel()
    {
        Assert.Contains("me:", TranscriptExport.ToSrt(Two));
        Assert.Contains("them:", TranscriptExport.ToVtt(Two));
    }

    [Fact]
    public void ToSrt_ReturnsNothingUsable_ForAnEmptyTranscript()
    {
        Assert.True(string.IsNullOrWhiteSpace(TranscriptExport.ToSrt(Array.Empty<TranscriptSegment>())));
    }

    [Fact]
    public void ToVtt_StillWritesTheHeader_ForAnEmptyTranscript()
    {
        // Un VTT sin cabecera no lo abre ningun reproductor, aunque no tenga cues.
        Assert.StartsWith("WEBVTT", TranscriptExport.ToVtt(Array.Empty<TranscriptSegment>()));
    }

    [Fact]
    public void Formats_CollapseNewlinesInsideASegment()
    {
        // Una linea en blanco dentro de un cue lo termina antes de tiempo en SRT.
        var segments = new[] { new TranscriptSegment("me", 0, 1_000, "una\n\nfrase\r\npartida") };

        foreach (var body in new[] { TranscriptExport.ToSrt(segments), TranscriptExport.ToVtt(segments) })
        {
            Assert.DoesNotContain("\n\n\n", body.ReplaceLineEndings("\n").Trim());
        }
    }

    [Theory]
    [InlineData("presupuesto", "presupuesto")]
    [InlineData("PRESUPUESTO", "presupuesto")]
    [InlineData("Presupuésto", "presupuesto")]
    [InlineData("  reunion  ", "reunion")]
    public void Normalize_FoldsCaseAndAccents_SoLaBusquedaEncuentraIgual(string input, string expected)
    {
        Assert.Equal(expected, TranscriptSearch.Normalize(input));
    }

    [Fact]
    public void Matches_FindsAccentedTextFromAnUnaccentedQuery()
    {
        Assert.True(TranscriptSearch.Matches("Revisamos el presupuesto anual", TranscriptSearch.Normalize("PRESUPUESTO")));
        Assert.False(TranscriptSearch.Matches("Revisamos el calendario", TranscriptSearch.Normalize("presupuesto")));
    }

    [Fact]
    public void Matches_IsFalseForEmptyText()
    {
        Assert.False(TranscriptSearch.Matches(null, "algo"));
        Assert.False(TranscriptSearch.Matches("   ", "algo"));
    }

    [Fact]
    public void ToSrt_RendersHoursForLongMeetings()
    {
        var segments = new[] { new TranscriptSegment("me", 3_723_000, 3_725_000, "hora larga") };

        Assert.Contains("01:02:03,000", TranscriptExport.ToSrt(segments));
    }
}
