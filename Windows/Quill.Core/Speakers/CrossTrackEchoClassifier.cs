using System.Globalization;
using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Speakers;

/// <summary>Thresholds of <see cref="CrossTrackEchoClassifier"/>. Defaults are tuned on synthetic cases.</summary>
public sealed record EchoClassifierOptions
{
    /// <summary>Slack applied to system segment bounds when looking for time overlap (ASR timestamps drift).</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Text overlap that alone makes a segment echo.</summary>
    public double StrongTextOverlap { get; init; } = 0.8;

    /// <summary>Mic segments with fewer tokens ("yes", "ok") are never echo on text alone.</summary>
    public int MinTokensForTextEvidence { get; init; } = 3;

    /// <summary>Energy correlation that alone makes a segment echo.</summary>
    public double StrongEnergyCorrelation { get; init; } = 0.75;

    /// <summary>Text and energy both at least this high make a segment echo.</summary>
    public double CombinedThreshold { get; init; } = 0.5;

    /// <summary>Largest acoustic delay between the system signal and its copy in the mic.</summary>
    public TimeSpan MaxLag { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Shortest window the correlation is computed on.</summary>
    public TimeSpan MinEnergyWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Mean system RMS below this means the system track is silent there (nothing to leak).</summary>
    public float SilentRms { get; init; } = 0.01f;
}

/// <summary>
/// Flags microphone segments that are remote audio leaking from the speakers into the
/// mic. Two independent signals: the mic text repeats a time-overlapping system segment,
/// and/or the mic energy envelope follows the system envelope with a 0–300 ms delay.
/// Headphones give neither; genuine crosstalk overlaps in time but differs in both.
/// </summary>
public sealed class CrossTrackEchoClassifier(EchoClassifierOptions? options = null) : IEchoClassifier
{
    private readonly EchoClassifierOptions _options = options ?? new EchoClassifierOptions();

    public Task<IReadOnlyList<EchoVerdict>> ClassifyAsync(
        EchoClassificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verdicts = new List<EchoVerdict>(request.MicrophoneSegments.Count);
        for (var index = 0; index < request.MicrophoneSegments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            verdicts.Add(Classify(index, request));
        }

        return Task.FromResult<IReadOnlyList<EchoVerdict>>(verdicts);
    }

    private EchoVerdict Classify(int index, EchoClassificationRequest request)
    {
        var mic = request.MicrophoneSegments[index];
        var text = BestTextMatch(mic, request.SystemSegments);
        var energy = request.MicrophoneEnergy is { } micEnergy && request.SystemEnergy is { } systemEnergy
            ? EnergyCorrelation(mic, micEnergy, systemEnergy)
            : null;

        var enoughTokens = TextOverlap.Tokens(mic.Text).Count >= _options.MinTokensForTextEvidence;
        var textScore = text?.Score ?? 0;
        var textEvidence = enoughTokens ? textScore : textScore * 0.5;
        var energyScore = Math.Max(0, energy?.Correlation ?? 0);

        var reasons = new List<string>();
        if (text is { } match)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"text overlap {match.Score:0.00} with system segment {match.Index}{(enoughTokens ? "" : " (too short)")}"));
        }
        else
        {
            reasons.Add("no overlapping system speech");
        }

        if (energy is { } correlation)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"energy correlation {correlation.Correlation:0.00} at {correlation.Lag.TotalMilliseconds:0} ms lag"));
        }

        var isEcho = textEvidence >= _options.StrongTextOverlap
            || energyScore >= _options.StrongEnergyCorrelation
            || (textEvidence >= _options.CombinedThreshold && energyScore >= _options.CombinedThreshold);

        var confidence = isEcho
            ? 1 - (1 - Math.Clamp(textEvidence, 0, 1)) * (1 - Math.Clamp(energyScore, 0, 1))
            : 1 - Math.Max(Math.Clamp(textEvidence, 0, 1), Math.Clamp(energyScore, 0, 1));

        return new EchoVerdict(index, isEcho, Math.Round(Math.Clamp(confidence, 0, 1), 3), string.Join("; ", reasons));
    }

    private (int Index, double Score)? BestTextMatch(TranscriptSegment mic, IReadOnlyList<TranscriptSegment> system)
    {
        (int Index, double Score)? best = null;
        for (var i = 0; i < system.Count; i++)
        {
            var candidate = system[i];
            var overlaps = candidate.Start - _options.TimeTolerance < mic.End
                && candidate.End + _options.TimeTolerance > mic.Start;
            if (!overlaps)
            {
                continue;
            }

            var score = TextOverlap.Score(mic.Text, candidate.Text);
            if (best is null || score > best.Value.Score)
            {
                best = (i, score);
            }
        }

        return best;
    }

    /// <summary>
    /// Max Pearson correlation of mic[t] with system[t - lag] over the mic segment window,
    /// for lag in [0, MaxLag]. Null when the window is too short, the system track is
    /// silent there, or either signal is flat.
    /// </summary>
    private (double Correlation, TimeSpan Lag)? EnergyCorrelation(
        TranscriptSegment mic,
        EnergyEnvelope micEnergy,
        EnergyEnvelope systemEnergy)
    {
        if (micEnergy.FrameDuration != systemEnergy.FrameDuration || micEnergy.FrameDuration <= TimeSpan.Zero)
        {
            return null;
        }

        var frame = micEnergy.FrameDuration;
        var first = (int)(mic.Start.Ticks / frame.Ticks);
        var last = Math.Min(micEnergy.Rms.Count, (int)(mic.End.Ticks / frame.Ticks));
        var maxLag = (int)(_options.MaxLag.Ticks / frame.Ticks);
        if (last - first < _options.MinEnergyWindow.Ticks / frame.Ticks)
        {
            return null;
        }

        (double Correlation, TimeSpan Lag)? best = null;
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var start = Math.Max(first, lag);
            var end = Math.Min(last, systemEnergy.Rms.Count + lag);
            if (end - start < _options.MinEnergyWindow.Ticks / frame.Ticks)
            {
                continue;
            }

            double systemMean = 0;
            for (var t = start; t < end; t++)
            {
                systemMean += systemEnergy.Rms[t - lag];
            }

            systemMean /= end - start;
            if (systemMean < _options.SilentRms)
            {
                continue;
            }

            var correlation = Pearson(micEnergy.Rms, systemEnergy.Rms, start, end, lag);
            if (correlation is { } value && (best is null || value > best.Value.Correlation))
            {
                best = (value, TimeSpan.FromTicks(frame.Ticks * lag));
            }
        }

        return best;
    }

    private static double? Pearson(IReadOnlyList<float> mic, IReadOnlyList<float> system, int start, int end, int lag)
    {
        var n = end - start;
        double sumX = 0, sumY = 0;
        for (var t = start; t < end; t++)
        {
            sumX += mic[t];
            sumY += system[t - lag];
        }

        var meanX = sumX / n;
        var meanY = sumY / n;
        double covariance = 0, varianceX = 0, varianceY = 0;
        for (var t = start; t < end; t++)
        {
            var dx = mic[t] - meanX;
            var dy = system[t - lag] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        const double flat = 1e-12;
        return varianceX < flat || varianceY < flat ? null : covariance / Math.Sqrt(varianceX * varianceY);
    }
}
