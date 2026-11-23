using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Quill.Core.Ask;

/// <summary>
/// Prompts for Ask. Like the summary prompts, instructions are in Spanish and the answer
/// language is an explicit instruction. Every passage line is shown as
/// "[sessionId@hh:mm:ss] speakerId: text" so the model can copy the marker as a citation.
/// </summary>
public static partial class AskPrompts
{
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = "español",
        ["en"] = "inglés",
        ["fr"] = "francés",
        ["de"] = "alemán",
        ["it"] = "italiano",
        ["pt"] = "portugués",
        ["ca"] = "catalán",
        ["eu"] = "euskera",
        ["gl"] = "gallego",
        ["nl"] = "neerlandés",
    };

    /// <summary>The fixed "nothing found" answer. Spanish unless the answer language is English.</summary>
    public static string NotFoundText(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "I can't find that in your meetings."
            : "No lo encuentro en tus reuniones.";

    public static string System(string language, bool singleMeeting)
    {
        var output = new StringBuilder();
        output.AppendLine(singleMeeting
            ? "Eres el asistente de reuniones de Quill. Respondes preguntas sobre una reunión usando solo los fragmentos de su transcripción, resumen y notas que se te dan."
            : "Eres el asistente de reuniones de Quill. Respondes preguntas sobre varias reuniones usando solo los fragmentos que se te dan.");
        output.AppendLine();
        output.Append("Idioma de la respuesta: ").Append(LanguageName(language))
            .Append(" (código \"").Append(language).AppendLine("\"), aunque los fragmentos estén en otro idioma.");
        output.AppendLine();
        output.AppendLine("Reglas estrictas:");
        output.AppendLine("1. Usa solo lo que dicen los fragmentos. No inventes nombres, fechas, cifras, decisiones ni tareas.");
        output.AppendLine("2. Después de cada afirmación cita la línea de la que sale, copiando su marca tal cual con el formato [sesión@hh:mm:ss], por ejemplo [2026-11-20_10-00-00@00:12:05]. No cites marcas que no aparezcan en los fragmentos.");
        output.Append("3. Si los fragmentos no contienen la respuesta, responde exactamente «").Append(NotFoundText(language)).AppendLine("» y nada más.");
        output.AppendLine("4. Sé breve: de una a cinco frases, en prosa, sin títulos ni JSON.");
        output.AppendLine("5. \"me\" es el usuario; \"speaker-1\", \"speaker-2\"... son otros participantes. Las líneas con «(sin marca)» no se pueden citar.");
        return output.ToString();
    }

    /// <summary>Passages grouped by meeting, in the given order, then the question.</summary>
    public static string User(string question, IReadOnlyList<AskSource> sources)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(sources);
        var output = new StringBuilder("Fragmentos:\n");
        foreach (var meeting in sources.GroupBy(source => source.Passage.SessionId, StringComparer.Ordinal))
        {
            var first = meeting.First();
            output.Append("\n### Reunión «").Append(first.SessionTitle).Append('»');
            if (first.Started is { } started)
            {
                output.Append(" del ").Append(started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            output.Append(" (sesión ").Append(meeting.Key).AppendLine(")");
            foreach (var source in meeting)
            {
                output.AppendLine(Lines(source.Passage.SessionId, source.Passage.Text));
            }
        }

        output.Append("\nPregunta: ").AppendLine(question.Trim());
        return output.ToString();
    }

    /// <summary>
    /// Rewrites "[hh:mm:ss] ..." lines to "[sessionId@hh:mm:ss] ..."; lines without a timestamp
    /// get "(sin marca)".
    /// </summary>
    public static string Lines(string sessionId, string passageText)
    {
        var lines = passageText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => LineTimestamp().IsMatch(line)
                ? LineTimestamp().Replace(line, $"[{sessionId}@$1]", 1)
                : $"(sin marca) {line}");
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^\[(\d{1,3}:\d{2}:\d{2})\]")]
    internal static partial Regex LineTimestamp();

    private static string LanguageName(string code) =>
        LanguageNames.TryGetValue(code, out var name) ? name : code;
}
