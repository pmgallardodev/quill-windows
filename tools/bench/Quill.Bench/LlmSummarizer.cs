using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Quill.Bench;

public sealed record RunMetrics(
    string Model,
    string Meeting,
    bool Completed,
    string? Error,
    int PromptTokens,
    int OutputTokens,
    double LoadSeconds,
    double PrefillSeconds,
    double DecodeSeconds,
    double WallSeconds)
{
    public double PrefillTokensPerSecond => PrefillSeconds > 0 ? PromptTokens / PrefillSeconds : 0;
    public double DecodeTokensPerSecond => DecodeSeconds > 0 ? OutputTokens / DecodeSeconds : 0;
}

/// <summary>Runs one GGUF model over the corpus with grammar-constrained decoding (LLamaSharp).</summary>
public static class LlmSummarizer
{
    /// <summary>
    /// Must run before any other LLamaSharp call. "cpu"/"vulkan" pick that backend (falling back to the
    /// CPU builds when Vulkan cannot load); "auto" keeps LLamaSharp's own detection.
    /// </summary>
    public static void ConfigureBackend(string backend)
    {
        var config = NativeLibraryConfig.All.WithLogCallback((level, message) =>
        {
            if (level >= LLamaLogLevel.Error ||
                message.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("GPU name", StringComparison.Ordinal) ||
                message.Contains("offloaded", StringComparison.Ordinal))
            {
                Console.Error.Write(message);
            }
        });
        if (backend == "auto")
        {
            return;
        }

        config.WithVulkan(backend == "vulkan").WithAutoFallback(true);
    }

    public static async Task<bool> RunAsync(string modelPath, string modelLabel, IReadOnlyList<Meeting> meetings,
        string outDirectory, int maxTokens, int gpuLayers)
    {
        Directory.CreateDirectory(outDirectory);
        var allCompleted = true;

        var loadClock = Stopwatch.StartNew();
        var modelParams = new ModelParams(modelPath) { GpuLayerCount = gpuLayers, ContextSize = 4096 };
        using var weights = await LLamaWeights.LoadFromFileAsync(modelParams);
        var loadSeconds = loadClock.Elapsed.TotalSeconds;
        Console.WriteLine($"[{modelLabel}] loaded in {loadSeconds:F1}s; gpu offload supported: " +
                          $"{NativeApi.llama_supports_gpu_offload()}; system: {NativeApi.llama_print_system_info()}");

        foreach (var meeting in meetings)
        {
            var outPath = Path.Combine(outDirectory, $"{meeting.Id}.json");
            var metricsPath = Path.Combine(outDirectory, $"{meeting.Id}.metrics.json");
            if (File.Exists(metricsPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<RunMetrics>(File.ReadAllText(metricsPath));
                    allCompleted &= existing?.Completed == true;
                }
                catch (JsonException)
                {
                    allCompleted = false;
                }

                Console.WriteLine($"[{modelLabel}] {meeting.Id}: already done, skipping");
                continue;
            }

            RunMetrics metrics;
            try
            {
                metrics = await SummarizeAsync(weights, modelPath, modelLabel, meeting, outPath, maxTokens, gpuLayers, loadSeconds);
            }
            catch (Exception ex)
            {
                metrics = new RunMetrics(modelLabel, meeting.Id, false, ex.GetType().Name + ": " + ex.Message, 0, 0,
                    loadSeconds, 0, 0, 0);
            }

            File.WriteAllText(metricsPath, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            allCompleted &= metrics.Completed;
            Console.WriteLine(
                $"[{modelLabel}] {meeting.Id}: ok={metrics.Completed} prompt={metrics.PromptTokens} out={metrics.OutputTokens} " +
                $"prefill={metrics.PrefillTokensPerSecond:F0}t/s decode={metrics.DecodeTokensPerSecond:F1}t/s wall={metrics.WallSeconds:F0}s " +
                metrics.Error);
        }

        return allCompleted;
    }

    private static async Task<RunMetrics> SummarizeAsync(LLamaWeights weights, string modelPath, string modelLabel, Meeting meeting,
        string outPath, int maxTokens, int gpuLayers, double loadSeconds)
    {
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", SummaryPrompt.System);
        template.Add("user", SummaryPrompt.User(meeting));
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var promptTokens = weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
        var contextSize = (uint)(Math.Ceiling((promptTokens + maxTokens + 64) / 256.0) * 256);
        var contextParams = new ModelParams(modelPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = gpuLayers,
            BatchSize = 512,
            UBatchSize = 512,
        };

        var executor = new StatelessExecutor(weights, contextParams) { ApplyTemplate = false };
        var inference = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Grammar = new Grammar(SummaryPrompt.Grammar(meeting.Key.Speakers.Keys), "root"),
                Temperature = 0.2f,
                Seed = 42,
                RepeatPenalty = 1.0f,
            },
        };

        var output = new StringBuilder();
        var tokens = 0;
        var wall = Stopwatch.StartNew();
        double firstTokenSeconds = 0;
        await foreach (var piece in executor.InferAsync(prompt, inference))
        {
            if (tokens == 0)
            {
                firstTokenSeconds = wall.Elapsed.TotalSeconds;
            }

            tokens++;
            output.Append(piece);
        }

        wall.Stop();
        var text = output.ToString();
        File.WriteAllText(outPath, text);

        string? error = null;
        try
        {
            using var _ = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
        }

        return new RunMetrics(modelLabel, meeting.Id, error is null, error, promptTokens, tokens, loadSeconds,
            firstTokenSeconds, wall.Elapsed.TotalSeconds - firstTokenSeconds, wall.Elapsed.TotalSeconds);
    }
}
