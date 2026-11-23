using System.Text;

namespace Quill.Bench;

/// <summary>
/// Spanish prompt and GBNF grammar for the MeetingSummary contract (design §7.1).
/// The <c>meta</c> block is filled by the app, not generated, so it is not part of the grammar.
/// </summary>
public static class SummaryPrompt
{
    public const string System = """
        Eres un asistente que resume reuniones de trabajo en español. Respondes solo con un objeto JSON.
        Reglas:
        1. Usa solo información dicha explícitamente en la transcripción. No inventes nada.
        2. decisions: solo decisiones acordadas. Si una decisión se cambió más tarde, incluye solo la versión final.
        3. actionItems: tareas que alguien se compromete a hacer. "owner" es el id del hablante responsable
           (por ejemplo "me" o "speaker-2"); usa null si el responsable no quedó claro. "isMine" es true solo si
           owner es "me". "due" es el plazo mencionado o null.
        4. openQuestions: preguntas que quedaron sin respuesta al terminar la reunión.
        5. risks: riesgos, bloqueos o preocupaciones mencionados.
        6. "at": copia exactamente la marca de tiempo hh:mm:ss de la línea donde se dice.
        7. executiveSummary: de 3 a 5 frases.
        8. Si una sección no tiene elementos, devuelve una lista vacía.
        """;

    public static string User(Meeting meeting)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Participantes (id: nombre):");
        foreach (var (id, name) in meeting.Key.Speakers)
        {
            sb.AppendLine($"- {id}: {name}");
        }

        sb.AppendLine();
        sb.AppendLine("Transcripción:");
        foreach (var s in meeting.Transcript.Segments)
        {
            sb.Append('[').Append(Corpus.FormatTime(s.StartMs)).Append("] ").Append(s.Speaker).Append(": ")
              .AppendLine(s.Text);
        }

        sb.AppendLine();
        sb.Append("Devuelve el JSON del resumen final de la reunión.");
        return sb.ToString();
    }

    /// <summary>GBNF for llama.cpp. Speaker ids are restricted to the meeting's participants.</summary>
    public static string Grammar(IEnumerable<string> speakerIds, int maxItems = 12)
    {
        var speakers = string.Join(" | ", speakerIds.Select(id => $"\"\\\"{id}\\\"\""));
        var more = $"{{0,{maxItems - 1}}}";
        return $$"""
            root ::= "{" ws "\"executiveSummary\":" ws str "," ws "\"decisions\":" ws decisions "," ws "\"actionItems\":" ws actions "," ws "\"openQuestions\":" ws textats "," ws "\"topics\":" ws topics "," ws "\"risks\":" ws textats "," ws "\"keyFacts\":" ws textats "," ws "\"nextSteps\":" ws strs ws "}"
            decisions ::= "[" ws ( decision ( "," ws decision ){{more}} )? ws "]"
            decision ::= "{" ws "\"text\":" ws str "," ws "\"decidedBy\":" ws spk "," ws "\"at\":" ws time ws "}"
            actions ::= "[" ws ( action ( "," ws action ){{more}} )? ws "]"
            action ::= "{" ws "\"text\":" ws str "," ws "\"owner\":" ws spk "," ws "\"due\":" ws strornull "," ws "\"isMine\":" ws bool "," ws "\"at\":" ws time "," ws "\"done\":" ws "false" ws "}"
            textats ::= "[" ws ( textat ( "," ws textat ){{more}} )? ws "]"
            textat ::= "{" ws "\"text\":" ws str "," ws "\"at\":" ws time ws "}"
            topics ::= "[" ws ( topic ( "," ws topic ){{more}} )? ws "]"
            topic ::= "{" ws "\"title\":" ws str "," ws "\"start\":" ws time ws "}"
            strs ::= "[" ws ( str ( "," ws str ){{more}} )? ws "]"
            spk ::= {{speakers}} | "null"
            strornull ::= str | "null"
            bool ::= "true" | "false"
            time ::= "\"" [0-9] [0-9] ":" [0-5] [0-9] ":" [0-5] [0-9] "\""
            str ::= "\"" char+ "\""
            char ::= [^"\\\x00-\x1f] | "\\" ["\\/nt]
            ws ::= [ \n]?
            """;
    }
}
