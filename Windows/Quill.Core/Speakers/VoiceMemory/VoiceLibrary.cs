namespace Quill.Core.Speakers.VoiceMemory;

/// <summary>
/// Thresholds of speaker-naming memory (F11). Cluster centroids from the sherpa-onnx speaker
/// embedding model score roughly 0.7-0.9 for the same person across meetings and well under
/// 0.5 for different people, so the defaults only suggest clear matches and stay silent when
/// two known voices (or the owner's) are close.
/// </summary>
public sealed record VoiceMatchOptions
{
    /// <summary>Minimum cosine similarity between a speaker and a saved voice to suggest its name.</summary>
    public double MinSimilarity { get; init; } = 0.65;

    /// <summary>The best voice must beat the runner-up (including the owner's voice) by this much.</summary>
    public double MinMargin { get; init; } = 0.10;

    /// <summary>With "aplicar automáticamente" on, only matches at least this similar are applied without asking.</summary>
    public double AutoApplySimilarity { get; init; } = 0.80;

    /// <summary>Most recent samples kept per voice (older ones are dropped from the mean).</summary>
    public int MaxSamplesPerVoice { get; init; } = 30;

    /// <summary>Normal meetings whose microphone voice is sampled before the owner voice counts as learned.</summary>
    public int OwnerLearningSessions { get; init; } = 5;
}

/// <summary>One observation of a voice: the normalized cluster centroid of a speaker in one meeting.</summary>
/// <param name="SpeakerId">"speaker-N" for named voices; "me" for the owner.</param>
public sealed record VoiceSample(string SessionId, string SpeakerId, float[] Embedding, DateTimeOffset At);

/// <summary>
/// A saved voice: a name the user gave (or the owner's own voice, which has no name) plus the
/// samples it was learned from. <see cref="Embedding"/> is the mean of the normalized samples,
/// recomputed from them, so a sample can be removed again when the user changes a name.
/// </summary>
public sealed record VoicePrint(string Id, string? Name, bool IsOwner, IReadOnlyList<VoiceSample> Samples)
{
    // Computed on read: a cached value would survive `with { Samples = ... }` copies.
    public float[] Embedding => Mean(Samples);

    public int Count => Samples.Count;

    public DateTimeOffset LastSeen => Samples.Count == 0 ? default : Samples.Max(sample => sample.At);

    /// <summary>Session ids the voice was heard in, in learning order.</summary>
    public IReadOnlyList<string> Sessions => Samples.Select(sample => sample.SessionId).Distinct(StringComparer.Ordinal).ToList();

    private static float[] Mean(IReadOnlyList<VoiceSample> samples)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        // A model change gives new samples another size: the newest size wins.
        var size = samples[^1].Embedding.Length;
        var usable = samples.Where(sample => sample.Embedding.Length == size).ToList();
        var mean = new float[size];
        foreach (var sample in usable)
        {
            for (var i = 0; i < size; i++)
            {
                mean[i] += sample.Embedding[i];
            }
        }

        for (var i = 0; i < size; i++)
        {
            mean[i] /= usable.Count;
        }

        return mean;
    }
}

/// <summary>A speaker of one meeting that has no name yet.</summary>
public sealed record UnnamedSpeaker(string SpeakerId, float[]? Embedding);

/// <summary>The user said "No es" to <paramref name="VoiceId"/> for <paramref name="SpeakerId"/> in one meeting.</summary>
public sealed record VoiceRejection(string SpeakerId, string VoiceId);

/// <summary>"¿Es Juan?": <paramref name="Name"/> of voice <paramref name="VoiceId"/> proposed for <paramref name="SpeakerId"/>.</summary>
public sealed record VoiceSuggestion(string SpeakerId, string VoiceId, string Name, double Similarity);

/// <summary>
/// Local voice memory (voices.json): named voices learned when the user names a speaker, and
/// the owner's voice learned from the microphone. Immutable: every operation returns a new
/// library. Matching only ever produces suggestions; applying one is the caller's choice.
/// </summary>
public sealed class VoiceLibrary
{
    private static readonly VoiceMatchOptions Defaults = new();

    public VoiceLibrary(IReadOnlyList<VoicePrint> voices, bool autoApply)
    {
        Voices = voices;
        AutoApply = autoApply;
    }

    public static VoiceLibrary Empty { get; } = new([], false);

    public IReadOnlyList<VoicePrint> Voices { get; }

    /// <summary>"Aplicar automáticamente": confident matches are applied as names. Off by default.</summary>
    public bool AutoApply { get; }

    /// <summary>The owner's own voice, or null while none was learned.</summary>
    public VoicePrint? Owner => Voices.FirstOrDefault(voice => voice.IsOwner);

    /// <summary>Named voices, alphabetically.</summary>
    public IReadOnlyList<VoicePrint> Named =>
        Voices.Where(voice => !voice.IsOwner).OrderBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public VoiceLibrary WithAutoApply(bool autoApply) => new(Voices, autoApply);

    /// <summary>
    /// The user named <paramref name="speakerId"/> of <paramref name="sessionId"/> <paramref name="name"/>:
    /// adds that speaker's embedding to the voice with that name (case-insensitive) or creates it.
    /// A previous name of the same speaker loses the sample.
    /// </summary>
    public VoiceLibrary Learn(string name, string sessionId, string speakerId, float[] embedding, DateTimeOffset at, VoiceMatchOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = Normalize(embedding);
        var library = Unlearn(sessionId, speakerId);
        if (normalized is null)
        {
            return library;
        }

        var sample = new VoiceSample(sessionId, speakerId, normalized, at);
        var trimmed = name.Trim();
        var existing = library.Voices.FirstOrDefault(voice => !voice.IsOwner && SameName(voice.Name, trimmed));
        var voices = existing is null
            ? library.Voices.Append(new VoicePrint(NewId(), trimmed, false, [sample])).ToList()
            : library.Voices.Select(voice => voice == existing ? WithSample(voice, sample, options) : voice).ToList();
        return new VoiceLibrary(voices, AutoApply);
    }

    /// <summary>Removes the sample of <paramref name="speakerId"/> in <paramref name="sessionId"/> from named voices.</summary>
    public VoiceLibrary Unlearn(string sessionId, string speakerId)
    {
        var voices = Voices
            .Select(voice => voice.IsOwner
                ? voice
                : voice with { Samples = voice.Samples.Where(sample => sample.SessionId != sessionId || sample.SpeakerId != speakerId).ToList() })
            .Where(voice => voice.IsOwner || voice.Samples.Count > 0)
            .ToList();
        return new VoiceLibrary(voices, AutoApply);
    }

    /// <summary>Adds (or replaces) this session's sample of the owner's voice.</summary>
    public VoiceLibrary LearnOwner(string sessionId, float[] embedding, DateTimeOffset at, VoiceMatchOptions? options = null)
    {
        var normalized = Normalize(embedding);
        if (normalized is null)
        {
            return this;
        }

        var sample = new VoiceSample(sessionId, Domain.SpeakerIds.Me, normalized, at);
        var owner = Owner;
        var voices = owner is null
            ? Voices.Append(new VoicePrint(NewId(), null, true, [sample])).ToList()
            : Voices.Select(voice => voice == owner
                ? WithSample(voice with { Samples = voice.Samples.Where(s => s.SessionId != sessionId).ToList() }, sample, options)
                : voice).ToList();
        return new VoiceLibrary(voices, AutoApply);
    }

    /// <summary>True while the owner voice was heard in fewer than <see cref="VoiceMatchOptions.OwnerLearningSessions"/> meetings.</summary>
    public bool NeedsOwnerSamples(VoiceMatchOptions? options = null) =>
        (Owner?.Sessions.Count ?? 0) < (options ?? Defaults).OwnerLearningSessions;

    /// <summary>
    /// Suggestions for unnamed speakers. A voice is proposed only when it is at least
    /// <see cref="VoiceMatchOptions.MinSimilarity"/> similar and beats every other candidate,
    /// including the owner's voice, by <see cref="VoiceMatchOptions.MinMargin"/>. Rejected
    /// pairs and names already used in the meeting are skipped; a voice goes to one speaker at most.
    /// </summary>
    public IReadOnlyList<VoiceSuggestion> Suggest(
        IReadOnlyList<UnnamedSpeaker> speakers,
        IReadOnlyCollection<VoiceRejection> rejected,
        IReadOnlyCollection<string> namesInUse,
        VoiceMatchOptions? options = null)
    {
        options ??= Defaults;
        var named = Voices
            .Where(voice => !voice.IsOwner && voice.Name is not null && !namesInUse.Any(used => SameName(used, voice.Name)))
            .ToList();
        var owner = Owner;
        var proposals = new List<VoiceSuggestion>();
        foreach (var speaker in speakers)
        {
            if (speaker.Embedding is not { Length: > 0 } embedding)
            {
                continue;
            }

            var scored = named
                .Where(voice => !rejected.Contains(new VoiceRejection(speaker.SpeakerId, voice.Id)))
                .Select(voice => (Voice: voice, Score: Vectors.Cosine(embedding, voice.Embedding)))
                .OrderByDescending(pair => pair.Score)
                .ToList();
            if (scored.Count == 0 || scored[0].Score < options.MinSimilarity)
            {
                continue;
            }

            var runnerUp = Math.Max(
                scored.Count > 1 ? scored[1].Score : 0,
                owner is null ? 0 : Vectors.Cosine(embedding, owner.Embedding));
            if (scored[0].Score - runnerUp < options.MinMargin)
            {
                continue;
            }

            proposals.Add(new VoiceSuggestion(speaker.SpeakerId, scored[0].Voice.Id, scored[0].Voice.Name!, Math.Round(scored[0].Score, 3)));
        }

        return proposals
            .GroupBy(proposal => proposal.VoiceId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(proposal => proposal.Similarity).First())
            .OrderBy(proposal => speakers.Select(s => s.SpeakerId).ToList().IndexOf(proposal.SpeakerId))
            .ToList();
    }

    /// <summary>Removes a voice (named or the owner's) and everything learned for it.</summary>
    public VoiceLibrary Forget(string voiceId) =>
        new(Voices.Where(voice => voice.Id != voiceId).ToList(), AutoApply);

    /// <summary>Renames a named voice; a name another voice already has merges both.</summary>
    public VoiceLibrary Rename(string voiceId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var other = Voices.FirstOrDefault(voice => voice.Id != voiceId && !voice.IsOwner && SameName(voice.Name, trimmed));
        if (other is not null)
        {
            return Merge(voiceId, other.Id);
        }

        return new VoiceLibrary(
            Voices.Select(voice => voice.Id == voiceId && !voice.IsOwner ? voice with { Name = trimmed } : voice).ToList(),
            AutoApply);
    }

    /// <summary>Moves every sample of <paramref name="fromId"/> into <paramref name="intoId"/> and removes <paramref name="fromId"/>.</summary>
    public VoiceLibrary Merge(string fromId, string intoId)
    {
        var from = Voices.FirstOrDefault(voice => voice.Id == fromId);
        var into = Voices.FirstOrDefault(voice => voice.Id == intoId);
        if (from is null || into is null || from == into)
        {
            return this;
        }

        var merged = into with { Samples = into.Samples.Concat(from.Samples).OrderBy(sample => sample.At).ToList() };
        return new VoiceLibrary(
            Voices.Where(voice => voice != from).Select(voice => voice == into ? merged : voice).ToList(),
            AutoApply);
    }

    /// <summary>Unit-length copy; null for a missing, empty or silent embedding.</summary>
    public static float[]? Normalize(float[]? embedding)
    {
        if (embedding is not { Length: > 0 })
        {
            return null;
        }

        double norm = 0;
        foreach (var value in embedding)
        {
            norm += value * value;
        }

        if (norm <= 0 || double.IsNaN(norm) || double.IsInfinity(norm))
        {
            return null;
        }

        var length = Math.Sqrt(norm);
        return embedding.Select(value => (float)(value / length)).ToArray();
    }

    private static VoicePrint WithSample(VoicePrint voice, VoiceSample sample, VoiceMatchOptions? options)
    {
        var max = Math.Max(1, (options ?? Defaults).MaxSamplesPerVoice);
        var samples = voice.Samples.Append(sample)
            .Select((s, index) => (Sample: s, Index: index))
            .OrderBy(pair => pair.Sample.At)
            .ThenBy(pair => pair.Index)
            .Select(pair => pair.Sample)
            .ToList();
        return voice with { Samples = samples.Skip(Math.Max(0, samples.Count - max)).ToList() };
    }

    private static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
