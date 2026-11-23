using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Bench;

/// <summary>Same JSON shape as Quill's transcript.json (Windows/Quill.Windows/Sessions/SessionDocuments.cs).</summary>
public sealed record TranscriptSegment(
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("start_ms")] long StartMs,
    [property: JsonPropertyName("end_ms")] long EndMs,
    [property: JsonPropertyName("text")] string Text);

public sealed record Transcript(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments);

public sealed record KeyItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("keywords")] IReadOnlyList<string> Keywords,
    [property: JsonPropertyName("at_ms")] long AtMs,
    [property: JsonPropertyName("owner")] string? Owner);

public sealed record AnswerKey(
    [property: JsonPropertyName("meeting")] string Meeting,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("speakers")] Dictionary<string, string> Speakers,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("decisions")] IReadOnlyList<KeyItem> Decisions,
    [property: JsonPropertyName("actionItems")] IReadOnlyList<KeyItem> ActionItems,
    [property: JsonPropertyName("openQuestions")] IReadOnlyList<KeyItem> OpenQuestions,
    [property: JsonPropertyName("risks")] IReadOnlyList<KeyItem> Risks,
    [property: JsonPropertyName("superseded")] IReadOnlyList<KeyItem> Superseded);

public sealed record Meeting(string Id, string Directory, Transcript Transcript, AnswerKey Key);

public static class Corpus
{
    public static IReadOnlyList<Meeting> Load(string corpusDirectory, IReadOnlyCollection<string>? only = null)
    {
        var meetings = new List<Meeting>();
        foreach (var dir in Directory.GetDirectories(corpusDirectory).Order())
        {
            var id = Path.GetFileName(dir);
            if (only is { Count: > 0 } && !only.Any(o => id.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var transcriptPath = Path.Combine(dir, "transcript.json");
            var keyPath = Path.Combine(dir, "key.json");
            if (!File.Exists(transcriptPath) || !File.Exists(keyPath))
            {
                continue;
            }

            var transcript = JsonSerializer.Deserialize<Transcript>(File.ReadAllText(transcriptPath))!;
            var key = JsonSerializer.Deserialize<AnswerKey>(File.ReadAllText(keyPath))!;
            meetings.Add(new Meeting(id, dir, transcript, key));
        }

        return meetings;
    }

    public static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static bool TryParseTime(string? value, out long ms)
    {
        ms = 0;
        if (value is null)
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) ||
            !int.TryParse(parts[2], out var s) || m > 59 || s > 59)
        {
            return false;
        }

        ms = ((h * 60L + m) * 60 + s) * 1000;
        return true;
    }
}
