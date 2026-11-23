using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quill.Bench;

/// <summary>
/// Scores a model's MeetingSummary JSON against the answer key.
///
/// Matching: both the predicted item text and the key keywords are normalized (lower case, accents
/// removed, digits spelled out in Spanish, "%" to "por ciento", punctuation to spaces). A key item
/// is hit by a prediction when at least ceil(n/2) of its n keywords occur as substrings. Within a
/// category, predictions and key items are paired greedily by descending hit ratio (one-to-one).
///
/// Penalties:
/// - unmatched: predicted item (decisions/actions/questions/risks) that matches no key item of any
///   category nor a superseded decision. Possible invention or a true-but-unkeyed detail.
/// - ungrounded: unmatched item where fewer than half of its content stems (first 5 chars of words
///   with 5+ letters) appear anywhere in the transcript. Strong hallucination signal.
/// - stale: predicted decision that matches only a superseded (reversed) decision.
/// - wrong owner: matched action item whose owner or isMine disagrees with the key (null expected
///   when the meeting left the owner ambiguous).
/// - invalid citation: any item whose "at" is unparsable or not inside a transcript segment (±1 s);
///   this is what the design's citation validator would drop.
/// </summary>
public static class Scorer
{
    private static readonly string[] Categories = ["decisions", "actionItems", "openQuestions", "risks"];

    public sealed record Score(
        string Model, string Meeting, bool Valid,
        Dictionary<string, (int Hit, int Total)> Recall,
        int Predicted, int Unmatched, int Ungrounded, int Stale, int WrongOwners, int InvalidCitations,
        List<string> Notes)
    {
        public double MacroRecall => Recall.Values.Where(v => v.Total > 0).Average(v => (double)v.Hit / v.Total);
        public int Hits => Recall.Values.Sum(v => v.Hit);
        public int KeyTotal => Recall.Values.Sum(v => v.Total);
    }

    private sealed record Pred(string Category, string Text, string? Owner, bool? IsMine, string? At);

    public static Score ScoreRun(string model, Meeting meeting, string summaryJson)
    {
        var notes = new List<string>();
        var recall = Categories.ToDictionary(c => c, c => (0, KeyFor(meeting.Key, c).Count));
        List<Pred> preds;
        List<string?> allAts;
        try
        {
            (preds, allAts) = Parse(summaryJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new Score(model, meeting.Id, false, recall, 0, 0, 0, 0, 0, 0, ["unparsable: " + ex.Message]);
        }

        var corpusText = Normalize(string.Join(" ", meeting.Transcript.Segments.Select(s => s.Text)));
        var matchedPreds = new HashSet<Pred>();
        var wrongOwners = 0;

        foreach (var category in Categories)
        {
            var keys = KeyFor(meeting.Key, category);
            var candidates = preds.Where(p => p.Category == category).ToList();
            var pairs = (from p in candidates
                         from k in keys
                         let r = HitRatio(p.Text, k)
                         where r >= 0
                         orderby r descending
                         select (p, k)).ToList();
            var usedKeys = new HashSet<KeyItem>();
            foreach (var (p, k) in pairs)
            {
                if (matchedPreds.Contains(p) || usedKeys.Contains(k))
                {
                    continue;
                }

                matchedPreds.Add(p);
                usedKeys.Add(k);
                if (category == "actionItems")
                {
                    var expectedMine = k.Owner == "me";
                    if (p.Owner != k.Owner || (p.IsMine is { } mine && mine != expectedMine))
                    {
                        wrongOwners++;
                        notes.Add($"{k.Id} owner: got {p.Owner ?? "null"}/isMine={p.IsMine}, want {k.Owner ?? "null"}");
                    }
                }
            }

            recall[category] = (usedKeys.Count, keys.Count);
            foreach (var missed in keys.Where(k => !usedKeys.Contains(k)))
            {
                notes.Add($"missed {missed.Id}: {missed.Text}");
            }
        }

        var unmatched = 0;
        var ungrounded = 0;
        var stale = 0;
        foreach (var p in preds.Where(p => !matchedPreds.Contains(p)))
        {
            if (p.Category == "decisions" && meeting.Key.Superseded.Any(s => HitRatio(p.Text, s) >= 0))
            {
                stale++;
                notes.Add($"stale decision: {p.Text}");
                continue;
            }

            // Matching a key item of another category (e.g. a risk reported as an open question) is
            // misfiled, not invented.
            if (Categories.Any(c => KeyFor(meeting.Key, c).Any(k => HitRatio(p.Text, k) >= 0)))
            {
                continue;
            }

            unmatched++;
            if (!IsGrounded(p.Text, corpusText))
            {
                ungrounded++;
                notes.Add($"ungrounded {p.Category}: {p.Text}");
            }
        }

        var invalidCitations = allAts.Count(at => !Corpus.TryParseTime(at, out var ms) ||
            !meeting.Transcript.Segments.Any(s => ms >= s.StartMs - 1000 && ms <= s.EndMs + 1000));

        return new Score(model, meeting.Id, true, recall, preds.Count, unmatched, ungrounded, stale, wrongOwners,
            invalidCitations, notes);
    }

    private static IReadOnlyList<KeyItem> KeyFor(AnswerKey key, string category) => category switch
    {
        "decisions" => key.Decisions,
        "actionItems" => key.ActionItems,
        "openQuestions" => key.OpenQuestions,
        "risks" => key.Risks,
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private static (List<Pred>, List<string?>) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var preds = new List<Pred>();
        var ats = new List<string?>();
        foreach (var category in Categories)
        {
            foreach (var item in root.GetProperty(category).EnumerateArray())
            {
                var at = item.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                string? owner = null;
                bool? isMine = null;
                if (category == "actionItems")
                {
                    owner = item.GetProperty("owner").ValueKind == JsonValueKind.String ? item.GetProperty("owner").GetString() : null;
                    isMine = item.GetProperty("isMine").GetBoolean();
                }

                preds.Add(new Pred(category, item.GetProperty("text").GetString() ?? "", owner, isMine, at));
                ats.Add(at);
            }
        }

        foreach (var name in new[] { "keyFacts" })
        {
            if (root.TryGetProperty(name, out var arr))
            {
                ats.AddRange(arr.EnumerateArray().Select(i => i.GetProperty("at").GetString()));
            }
        }

        if (root.TryGetProperty("topics", out var topics))
        {
            ats.AddRange(topics.EnumerateArray().Select(i => i.GetProperty("start").GetString()));
        }

        return (preds, ats);
    }

    /// <summary>Returns the hit ratio when the key is matched, or -1 when below threshold.</summary>
    private static double HitRatio(string predicted, KeyItem key)
    {
        var text = Normalize(predicted);
        var hits = key.Keywords.Count(kw => text.Contains(Normalize(kw), StringComparison.Ordinal));
        var needed = (int)Math.Ceiling(key.Keywords.Count / 2.0);
        return hits >= needed ? (double)hits / key.Keywords.Count : -1;
    }

    private static bool IsGrounded(string text, string normalizedCorpus)
    {
        var stems = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 5).Select(w => w[..5]).Distinct().ToList();
        if (stems.Count == 0)
        {
            return true;
        }

        return stems.Count(s => normalizedCorpus.Contains(s, StringComparison.Ordinal)) * 2 >= stems.Count;
    }

    private static readonly string[] Numbers =
    [
        "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez", "once", "doce",
        "trece", "catorce", "quince", "dieciseis", "diecisiete", "dieciocho", "diecinueve", "veinte",
        "veintiuno", "veintidos", "veintitres", "veinticuatro", "veinticinco", "veintiseis", "veintisiete",
        "veintiocho", "veintinueve", "treinta",
    ];

    public static string Normalize(string input)
    {
        var decomposed = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        var s = sb.ToString().Replace("%", " por ciento ");
        s = Regex.Replace(s, @"\d+", m =>
        {
            var n = int.Parse(m.Value, CultureInfo.InvariantCulture);
            return n switch
            {
                < 31 => Numbers[n],
                40 => "cuarenta",
                _ => m.Value,
            };
        });
        s = Regex.Replace(s, @"[^a-z0-9]+", " ");
        return " " + s.Trim() + " ";
    }
}
