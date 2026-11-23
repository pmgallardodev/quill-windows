using System.Text;
using System.Text.Json;
using Quill.Bench;

// Quill.Bench: standalone feasibility + model benchmark harness for Quill Next (task F0).
// Not referenced by the app. See tools/bench/RESULTS.md ("How to run") for usage.

var cmd = args.Length > 0 ? args[0] : "help";
var opts = ParseOptions(args.Skip(1).ToArray());
var benchRoot = FindBenchRoot();
var corpusDir = opts.GetValueOrDefault("corpus", Path.Combine(benchRoot, "corpus"));
var only = opts.TryGetValue("only", out var o) ? o.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];

switch (cmd)
{
    case "summarize":
    {
        var model = opts["model"];
        var label = opts.GetValueOrDefault("label", Path.GetFileNameWithoutExtension(model));
        LlmSummarizer.ConfigureBackend(opts.GetValueOrDefault("backend", "auto"));
        var outDir = Path.Combine(opts.GetValueOrDefault("out", Path.Combine(benchRoot, "results")), label);
        var completed = await LlmSummarizer.RunAsync(model, label, Corpus.Load(corpusDir, only), outDir,
            int.Parse(opts.GetValueOrDefault("max-tokens", "2500")), int.Parse(opts.GetValueOrDefault("gpu-layers", "99")));
        if (!completed)
        {
            Environment.ExitCode = 1;
        }

        break;
    }
    case "score":
    {
        var resultsDir = opts.GetValueOrDefault("results", Path.Combine(benchRoot, "results"));
        var report = ScoreAll(Corpus.Load(corpusDir, only), resultsDir);
        var outPath = opts.GetValueOrDefault("report", Path.Combine(resultsDir, "scores.md"));
        File.WriteAllText(outPath, report);
        Console.WriteLine(report);
        break;
    }
    case "grammar":
        Console.WriteLine(SummaryPrompt.Grammar(["me", "speaker-1"]));
        break;
    case "prompt":
        Console.WriteLine(SummaryPrompt.System);
        Console.WriteLine(SummaryPrompt.User(Corpus.Load(corpusDir, only)[0]));
        break;
    case "whisper":
        await AudioChecks.WhisperLatencyAsync(opts["model"], opts["audio"], opts.GetValueOrDefault("vad"),
            opts.GetValueOrDefault("chunks", "5,10").Split(',').Select(int.Parse).ToArray(),
            int.Parse(opts.GetValueOrDefault("repeats", "5")),
            int.Parse(opts.GetValueOrDefault("threads", "4")),
            bool.Parse(opts.GetValueOrDefault("gpu", "false")));
        break;
    case "diarize":
        AudioChecks.Diarize(opts["audio"], opts["segmentation"], opts["embedding"],
            int.Parse(opts.GetValueOrDefault("speakers", "-1")), int.Parse(opts.GetValueOrDefault("threads", "4")),
            int.Parse(opts.GetValueOrDefault("loop", "1")));
        break;
    default:
        Console.WriteLine("""
            usage:
              summarize --model <gguf> [--label name] [--only m1,m2] [--max-tokens 2500] [--gpu-layers 99] [--backend auto|cpu|vulkan]
              score     [--results dir] [--only m1]
              whisper   --model <ggml.bin> --audio <wav> [--vad silero.bin] [--chunks 5,10] [--threads 4] [--gpu false]
              diarize   --audio <wav> --segmentation <model.onnx> --embedding <model.onnx> [--speakers -1] [--threads 4] [--loop 1]
              grammar | prompt [--only m1]
            """);
        break;
}

static string ScoreAll(IReadOnlyList<Meeting> meetings, string resultsDir)
{
    var sb = new StringBuilder();
    var rows = new List<(string Model, Scorer.Score Score, RunMetrics? Metrics)>();
    foreach (var modelDir in Directory.GetDirectories(resultsDir).Order())
    {
        var model = Path.GetFileName(modelDir);
        foreach (var meeting in meetings)
        {
            var summaryPath = Path.Combine(modelDir, meeting.Id + ".json");
            var metricsPath = Path.Combine(modelDir, meeting.Id + ".metrics.json");
            if (!File.Exists(metricsPath))
            {
                continue;
            }

            var metrics = JsonSerializer.Deserialize<RunMetrics>(File.ReadAllText(metricsPath));
            var score = File.Exists(summaryPath)
                ? Scorer.ScoreRun(model, meeting, File.ReadAllText(summaryPath))
                : new Scorer.Score(model, meeting.Id, false, [], 0, 0, 0, 0, 0, 0, ["no output"]);
            rows.Add((model, score, metrics));
        }
    }

    sb.AppendLine("| model | meeting | valid | dec | act | q | risk | macro recall | pred | unmatched | ungrounded | stale | wrong owner | bad cite | prompt tok | out tok | prefill t/s | decode t/s | wall s |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (var (model, s, m) in rows)
    {
        string R(string c) => s.Recall.TryGetValue(c, out var v) ? $"{v.Hit}/{v.Total}" : "-";
        sb.AppendLine($"| {model} | {s.Meeting} | {(s.Valid ? "yes" : "NO")} | {R("decisions")} | {R("actionItems")} | " +
                      $"{R("openQuestions")} | {R("risks")} | {(s.Valid ? s.MacroRecall.ToString("P0") : "-")} | {s.Predicted} | " +
                      $"{s.Unmatched} | {s.Ungrounded} | {s.Stale} | {s.WrongOwners} | {s.InvalidCitations} | " +
                      $"{m?.PromptTokens} | {m?.OutputTokens} | {m?.PrefillTokensPerSecond:F0} | {m?.DecodeTokensPerSecond:F1} | {m?.WallSeconds:F0} |");
    }

    sb.AppendLine();
    sb.AppendLine("| model | runs | valid JSON | key hits | macro recall (mean) | unmatched | ungrounded | stale | wrong owners | bad citations | mean wall s |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (var g in rows.GroupBy(r => r.Model))
    {
        var valid = g.Where(r => r.Score.Valid).ToList();
        sb.AppendLine($"| {g.Key} | {g.Count()} | {valid.Count} | {g.Sum(r => r.Score.Hits)}/{g.Sum(r => r.Score.KeyTotal)} | " +
                      $"{(valid.Count > 0 ? valid.Average(r => r.Score.MacroRecall).ToString("P0") : "-")} | " +
                      $"{g.Sum(r => r.Score.Unmatched)} | {g.Sum(r => r.Score.Ungrounded)} | {g.Sum(r => r.Score.Stale)} | " +
                      $"{g.Sum(r => r.Score.WrongOwners)} | {g.Sum(r => r.Score.InvalidCitations)} | " +
                      $"{g.Average(r => r.Metrics?.WallSeconds ?? 0):F0} |");
    }

    sb.AppendLine();
    sb.AppendLine("<details><summary>Per-run notes</summary>");
    sb.AppendLine();
    foreach (var (model, s, _) in rows)
    {
        sb.AppendLine($"**{model} / {s.Meeting}**");
        foreach (var n in s.Notes)
        {
            sb.AppendLine($"- {n}");
        }

        sb.AppendLine();
    }

    sb.AppendLine("</details>");
    return sb.ToString();
}

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal))
        {
            d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal) ? a[++i] : "true";
        }
    }

    return d;
}

static string FindBenchRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "corpus", "generate.py")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
