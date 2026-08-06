using System.Globalization;
using System.Text;

namespace Quill.Windows.Sessions;

/// <summary>
/// Convierte segmentos a los formatos que entienden los reproductores y los
/// editores. Funciones puras: no tocan el disco ni dependen de la cultura, de
/// modo que el resultado es identico en cualquier maquina y se puede comparar
/// caracter a caracter en los tests.
/// </summary>
internal static class TranscriptExport
{
    /// <summary>
    /// Siempre "\n". Environment.NewLine haria que el archivo dependiese del
    /// sistema que lo genero, y todo reproductor acepta saltos de linea Unix.
    /// </summary>
    private const string NewLine = "\n";

    public const string SrtFileName = "transcript.srt";

    public const string VttFileName = "transcript.vtt";

    public static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var output = new StringBuilder();
        var index = 0;

        foreach (var cue in Cues(segments))
        {
            index++;
            output.Append(index.ToString(CultureInfo.InvariantCulture)).Append(NewLine);
            output.Append(Timestamp(cue.StartMs, ',')).Append(" --> ").Append(Timestamp(cue.EndMs, ',')).Append(NewLine);
            output.Append(cue.Body).Append(NewLine);
            output.Append(NewLine);
        }

        return output.ToString();
    }

    public static string ToVtt(IReadOnlyList<TranscriptSegment> segments)
    {
        var output = new StringBuilder();
        output.Append("WEBVTT").Append(NewLine).Append(NewLine);
        var index = 0;

        foreach (var cue in Cues(segments))
        {
            index++;
            output.Append(index.ToString(CultureInfo.InvariantCulture)).Append(NewLine);
            output.Append(Timestamp(cue.StartMs, '.')).Append(" --> ").Append(Timestamp(cue.EndMs, '.')).Append(NewLine);
            output.Append(cue.Body).Append(NewLine);
            output.Append(NewLine);
        }

        return output.ToString();
    }

    /// <summary>Una linea por segmento, en el mismo formato que muestra la ventana.</summary>
    public static string ToPlainText(IReadOnlyList<TranscriptSegment> segments)
    {
        var output = new StringBuilder();

        foreach (var segment in segments)
        {
            var text = Flatten(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }

            output.Append('[').Append(Clock(segment.StartMs)).Append("] ")
                  .Append(segment.Speaker).Append(": ")
                  .Append(text.Replace(NewLine, " ", StringComparison.Ordinal))
                  .Append(NewLine);
        }

        return output.ToString();
    }

    /// <summary>Reloj corto para la interfaz: m:ss, o h:mm:ss si la reunion pasa de la hora.</summary>
    public static string Clock(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(ClampMilliseconds(milliseconds));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:D2}");
    }

    /// <summary>Lee transcript.json y distingue un transcript vacio de uno corrupto.</summary>
    public static TranscriptReadResult Read(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "transcript.json");
        if (!File.Exists(path))
        {
            return new TranscriptReadResult(TranscriptReadStatus.Missing, Array.Empty<TranscriptSegment>());
        }

        return TranscriptDocument.TryRead(sessionDirectory, out var document)
            ? new TranscriptReadResult(
                document!.Segments.Count == 0 ? TranscriptReadStatus.Empty : TranscriptReadStatus.Valid,
                document.Segments)
            : new TranscriptReadResult(TranscriptReadStatus.Invalid, Array.Empty<TranscriptSegment>());
    }

    /// <summary>Lee transcript.json. Devuelve la lista vacia ante cualquier problema.</summary>
    public static IReadOnlyList<TranscriptSegment> TryReadSegments(string sessionDirectory)
    {
        return Read(sessionDirectory).Segments;
    }

    /// <summary>
    /// Un cue vacio o con una linea en blanco en medio rompe el archivo: la linea
    /// en blanco es justo lo que separa un cue del siguiente. Se aplanan aqui.
    /// </summary>
    private static IEnumerable<(long StartMs, long EndMs, string Body)> Cues(IReadOnlyList<TranscriptSegment> segments)
    {
        foreach (var segment in segments)
        {
            var text = Flatten(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var start = Math.Max(0, segment.StartMs);
            var end = Math.Max(start, segment.EndMs);
            yield return (start, end, $"{segment.Speaker}: {text}");
        }
    }

    private static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split('\n', '\r')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

        return string.Join(NewLine, lines);
    }

    private static string Timestamp(long milliseconds, char decimalSeparator)
    {
        var span = TimeSpan.FromMilliseconds(ClampMilliseconds(milliseconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}{decimalSeparator}{span.Milliseconds:D3}");
    }

    private static long ClampMilliseconds(long milliseconds) =>
        Math.Clamp(milliseconds, 0L, (long)TimeSpan.MaxValue.TotalMilliseconds);
}

internal enum TranscriptReadStatus
{
    Missing,
    Invalid,
    Empty,
    Valid,
}

internal sealed record TranscriptReadResult(
    TranscriptReadStatus Status,
    IReadOnlyList<TranscriptSegment> Segments);

/// <summary>
/// Busqueda sobre los transcript.json de todas las sesiones. Perezosa a proposito:
/// veinte reuniones largas no caben comodamente en memoria a la vez y el llamante
/// puede cortar en cuanto tenga suficientes resultados.
/// </summary>
internal static class TranscriptSearch
{
    /// <summary>
    /// Minusculas, sin diacriticos y sin espacios en los extremos, para que "sesion"
    /// encuentre "sesión". La forma D separa la letra de su acento y descartar los
    /// NonSpacingMark deja solo la base. El recorte importa porque esto normaliza
    /// tambien lo tecleado: un espacio de mas no debe dejar la busqueda sin resultados.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                output.Append(char.ToLowerInvariant(character));
            }
        }

        return output.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool Matches(string? text, string normalizedQuery) =>
        normalizedQuery.Length != 0 &&
        Normalize(text).Contains(normalizedQuery, StringComparison.Ordinal);

    /// <summary>
    /// Recorre las sesiones de la mas nueva a la mas vieja. Una sesion sin transcript
    /// o con el JSON corrupto se salta: buscar nunca debe reventar por un archivo roto.
    /// </summary>
    public static IEnumerable<TranscriptHit> Search(
        IReadOnlyList<SessionSnapshot> sessions,
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            yield break;
        }

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var segment in TranscriptExport.TryReadSegments(session.Directory))
            {
                if (Normalize(segment.Text).Contains(normalizedQuery, StringComparison.Ordinal))
                {
                    yield return new TranscriptHit(
                        session.Directory,
                        session.Title ?? session.Name,
                        segment.StartMs,
                        segment.Speaker,
                        segment.Text?.Trim() ?? string.Empty);
                }
            }
        }
    }
}

internal sealed record TranscriptHit(
    string Directory,
    string SessionLabel,
    long StartMs,
    string Speaker,
    string Text);
