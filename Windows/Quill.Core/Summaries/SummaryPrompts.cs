using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Storage;

namespace Quill.Core.Summaries;

/// <summary>Which step of the summary pipeline a prompt is for.</summary>
public enum SummaryPromptKind
{
    /// <summary>Whole transcript in one request.</summary>
    Final,

    /// <summary>Map step: one part of a long transcript.</summary>
    Partial,

    /// <summary>Reduce step: merge partial summaries.</summary>
    Merge,

    /// <summary>Incremental live update: previous live summary + new lines.</summary>
    Live,

    /// <summary>"¿Qué me perdí?": recap of the last minutes.</summary>
    CatchUp,
}

/// <summary>
/// Prompt texts for every summary step. Instructions are in Spanish (the owner's language and
/// the most common meeting language); the output language is a separate, explicit instruction.
/// Transcript lines always come from <see cref="Transcripts.TranscriptChunker.FormatLine"/>
/// ("[hh:mm:ss] speakerId: text") so the model can cite the exact timestamp.
/// </summary>
public static class SummaryPrompts
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

    /// <summary>Generation budget per step; detailed final summaries get more room.</summary>
    public static int MaxOutputTokens(SummaryPromptKind kind, SummaryLevel level) => kind switch
    {
        SummaryPromptKind.CatchUp => 512,
        SummaryPromptKind.Live => 1024,
        _ => level == SummaryLevel.Detailed ? 2048 : 1024,
    };

    public static string System(
        SummaryPromptKind kind,
        SummaryOptions options,
        IReadOnlyCollection<string> sections,
        IReadOnlyList<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(notes);

        var wanted = SummarySections.All
            .Where(section => section == SummarySections.ExecutiveSummary ||
                sections.Count == 0 ||
                sections.Contains(section, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var level = kind is SummaryPromptKind.Live or SummaryPromptKind.CatchUp ? SummaryLevel.Brief : options.ResolvedLevel;
        var output = new StringBuilder();

        output.AppendLine(Task(kind)).AppendLine();

        var language = options.ResolvedLanguage;
        output.Append("Idioma de salida: escribe todo el texto en ").Append(LanguageName(language))
            .Append(" (código \"").Append(language).AppendLine("\"), aunque la transcripción esté en otro idioma.")
            .AppendLine("Los identificadores de hablante y las marcas de tiempo se copian tal cual, sin traducir.")
            .AppendLine();

        output.AppendLine("Reglas estrictas:");
        output.AppendLine(kind == SummaryPromptKind.Merge
            ? "1. Usa solo lo que aparece en los resúmenes parciales. No añadas nada nuevo."
            : "1. Usa solo lo que se dice en la transcripción. No inventes decisiones, tareas, preguntas, nombres, fechas ni cifras.");
        output.AppendLine("2. Si una sección no tiene contenido, devuelve una lista vacía []. Mejor una lista vacía que un elemento dudoso.");
        output.AppendLine(kind == SummaryPromptKind.Merge
            ? "3. Conserva en \"at\" (o \"start\") la marca de tiempo hh:mm:ss que trae cada elemento; si fusionas duplicados, usa la más temprana."
            : "3. Cada elemento lleva en \"at\" (o \"start\" para un tema) la marca de tiempo exacta de la línea de la que procede, con formato hh:mm:ss y sin corchetes.");
        output.AppendLine("4. \"owner\" y \"decidedBy\" son el identificador del hablante tal como aparece (\"me\", \"speaker-1\"...), o null si no está claro. \"me\" es el usuario.");
        output.AppendLine("5. \"isMine\" es true solo si la tarea es del usuario (\"me\"). \"done\" es false salvo que se diga que ya está hecha.");
        output.AppendLine("6. Responde solo con el objeto JSON pedido.").AppendLine();

        output.AppendLine(level == SummaryLevel.Detailed
            ? "Nivel: detallado. Resumen ejecutivo de 4 a 6 frases; incluye todos los elementos relevantes con su contexto."
            : "Nivel: breve. Resumen ejecutivo de 2 o 3 frases; incluye solo los elementos importantes.");
        output.AppendLine();

        output.AppendLine("Secciones a rellenar:");
        foreach (var section in wanted)
        {
            output.Append("- ").Append(section).Append(": ").AppendLine(SectionHint(section));
        }

        if (kind is SummaryPromptKind.Final or SummaryPromptKind.Partial or SummaryPromptKind.Merge &&
            options.Template is { } template && !string.IsNullOrWhiteSpace(template.Body))
        {
            output.AppendLine()
                .Append("Indicaciones de la plantilla «").Append(template.Name).AppendLine("»:")
                .AppendLine(template.Body.Trim());
        }

        var renderedNotes = NotesDocument.Render(notes);
        if (renderedNotes.Length > 0 && kind != SummaryPromptKind.CatchUp)
        {
            output.AppendLine()
                .AppendLine("Notas del usuario (prioridad alta): señalan lo que el usuario considera importante. Úsalas para decidir qué incluir y cómo redactarlo,")
                .AppendLine("pero cada elemento debe seguir citando una línea de la transcripción; si la transcripción no respalda una nota, no la conviertas en un elemento.")
                .Append(renderedNotes);
        }

        return output.ToString().TrimEnd() + "\n";
    }

    /// <summary>User prompt for the whole transcript or one part of it.</summary>
    public static string Transcript(string lines, int part = 1, int parts = 1)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var header = parts > 1
            ? string.Create(CultureInfo.InvariantCulture, $"Transcripción (parte {part} de {parts}):")
            : "Transcripción:";
        return $"{header}\n{lines.TrimEnd()}\n";
    }

    public static string Merge(IReadOnlyList<MeetingSummary> partials, IReadOnlyCollection<string> sections)
    {
        ArgumentNullException.ThrowIfNull(partials);
        var output = new StringBuilder("Resúmenes parciales de la misma reunión, en orden:\n");
        for (var index = 0; index < partials.Count; index++)
        {
            output.Append(CultureInfo.InvariantCulture, $"\nParte {index + 1}:\n").AppendLine(ModelJson(partials[index], sections));
        }

        return output.ToString();
    }

    public static string Live(MeetingSummary? previous, string newLines)
    {
        ArgumentNullException.ThrowIfNull(newLines);
        var before = previous is null ? "(todavía no hay resumen)" : ModelJson(previous, SummarySections.Live);
        return $"Resumen anterior:\n{before}\n\nLíneas nuevas de la transcripción:\n{newLines.TrimEnd()}\n";
    }

    public static string CatchUp(string lines, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var minutes = Math.Max(1, (int)Math.Round(window.TotalMinutes));
        return string.Create(CultureInfo.InvariantCulture, $"Últimos {minutes} minutos de la transcripción:\n{lines.TrimEnd()}\n");
    }

    /// <summary>Compact JSON of the model-facing part of a summary: no meta, only <paramref name="sections"/>.</summary>
    public static string ModelJson(MeetingSummary summary, IReadOnlyCollection<string> sections)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(sections);
        var node = JsonNode.Parse(MeetingSummaryJson.Serialize(summary))!.AsObject();
        foreach (var key in node.Select(pair => pair.Key).ToList())
        {
            var keep = key == SummarySections.ExecutiveSummary ||
                (SummarySections.All.Contains(key) &&
                    (sections.Count == 0 || sections.Contains(key, StringComparer.OrdinalIgnoreCase)));
            if (!keep)
            {
                node.Remove(key);
            }
        }

        return node.ToJsonString(CompactJson);
    }

    private static string Task(SummaryPromptKind kind) => kind switch
    {
        SummaryPromptKind.Final =>
            "Eres un asistente que resume reuniones de trabajo. Resume la reunión a partir de su transcripción.",
        SummaryPromptKind.Partial =>
            "Eres un asistente que resume reuniones de trabajo. Resume esta parte de la transcripción de una reunión larga; otro paso unirá los resúmenes de todas las partes.",
        SummaryPromptKind.Merge =>
            "Eres un asistente que resume reuniones de trabajo. Fusiona los resúmenes parciales de una misma reunión en un único resumen: elimina duplicados y escribe un resumen ejecutivo de toda la reunión.",
        SummaryPromptKind.Live =>
            "Eres un asistente que mantiene un resumen en vivo de una reunión en curso. Recibes el resumen anterior y las líneas nuevas; devuelve el resumen actualizado: conserva lo anterior que siga siendo válido, añade lo nuevo y corrige lo que las líneas nuevas cambien.",
        SummaryPromptKind.CatchUp =>
            "Eres un asistente de reuniones. El usuario se ha distraído y pregunta «¿Qué me perdí?». Resume solo los últimos minutos, de forma muy breve.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string SectionHint(string section) => section switch
    {
        SummarySections.ExecutiveSummary => "resumen ejecutivo en prosa.",
        SummarySections.Decisions => "decisiones tomadas explícitamente, con quién decidió.",
        SummarySections.ActionItems => "tareas acordadas, con responsable y fecha límite (\"due\") solo si se dijo.",
        SummarySections.OpenQuestions => "preguntas que quedaron sin respuesta.",
        SummarySections.Topics => "temas tratados, en orden, con la marca de la línea donde empieza cada uno.",
        SummarySections.Risks => "riesgos o bloqueos mencionados.",
        SummarySections.KeyFacts => "datos clave dichos en la reunión (cifras, fechas, nombres).",
        SummarySections.NextSteps => "próximos pasos en frases cortas, sin marca de tiempo.",
        _ => string.Empty,
    };

    private static string LanguageName(string code) =>
        LanguageNames.TryGetValue(code, out var name) ? name : code;
}
