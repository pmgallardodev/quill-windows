using System.Text;
using Quill.Core.Ports;
using Quill.Core.Transcripts;

namespace Quill.Core.Ask;

/// <summary>Shared part of <see cref="MeetingAsk"/> and <see cref="LibraryAsk"/>: pack, prompt, stream, parse.</summary>
internal static class AskRunner
{
    /// <summary>Token budget left for passages once instructions, question and answer are counted.</summary>
    public static int PassageBudget(ILanguageModel model, AskOptions options, string question, bool singleMeeting)
    {
        var fixedTokens = TranscriptChunker.EstimateTokens(AskPrompts.System(options.Language, singleMeeting)) +
            TranscriptChunker.EstimateTokens(AskPrompts.User(question, []));
        return Math.Max(0, model.ContextTokens - options.MaxOutputTokens - options.SafetyMarginTokens - fixedTokens);
    }

    /// <summary>True when every source fits the budget together.</summary>
    public static bool Fits(IEnumerable<AskSource> sources, int budget) => sources.Sum(Cost) <= budget;

    /// <summary>Takes sources in order while they fit; the first one always goes in.</summary>
    public static IReadOnlyList<AskSource> Pack(IEnumerable<AskSource> ranked, int budget)
    {
        var packed = new List<AskSource>();
        var used = 0;
        foreach (var source in ranked)
        {
            var cost = Cost(source);
            if (packed.Count > 0 && used + cost > budget)
            {
                continue;
            }

            packed.Add(source);
            used += cost;
        }

        return packed;
    }

    /// <summary>Rewritten lines are longer ("sessionId@"), so they are measured as the model will see them.</summary>
    private static int Cost(AskSource source) =>
        TranscriptChunker.EstimateTokens(AskPrompts.Lines(source.Passage.SessionId, source.Passage.Text)) + 4;

    public static async Task<AskAnswer> RunAsync(
        ILanguageModel model,
        IInferenceScheduler scheduler,
        AskOptions options,
        string question,
        IReadOnlyList<AskSource> sources,
        string? defaultSessionId,
        IProgress<string>? tokens,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return AskAnswer.NothingFound(options.Language);
        }

        var request = new LanguageModelRequest(
            AskPrompts.System(options.Language, defaultSessionId is not null),
            AskPrompts.User(question, sources),
            options.MaxOutputTokens,
            options.Temperature);

        var raw = await scheduler.RunAsync(
            InferencePriority.Interactive,
            async token =>
            {
                var output = new StringBuilder();
                await foreach (var piece in model.GenerateAsync(request, token).ConfigureAwait(false))
                {
                    output.Append(piece);
                    tokens?.Report(piece);
                }

                return output.ToString();
            },
            cancellationToken).ConfigureAwait(false);

        return AskCitations.Parse(raw, sources, defaultSessionId, options.Language);
    }
}
