using System.Text.Json;
using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Speakers;

/// <summary>Reads an RMS energy envelope of a track, aligned to the session start (adapter: NAudio).</summary>
public interface IEnergyEnvelopeReader
{
    /// <summary>Null when the file cannot be read.</summary>
    Task<EnergyEnvelope?> ReadAsync(string audioPath, TimeSpan startOffset, CancellationToken cancellationToken);
}

public enum SpeakerResolutionStatus
{
    Resolved,
    SessionNotFound,
    NoTranscript,
}

public sealed record SpeakerResolutionOutcome(
    SpeakerResolutionStatus Status,
    int SpeakerCount = 0,
    int EchoSegmentsRemoved = 0,
    int EchoSegmentsReattributed = 0);

/// <summary>
/// "Identify speakers" use case: echo classification + diarization + <see cref="SpeakerResolver"/>,
/// then user edits replayed, persisted to transcript.json, speakers.json and
/// speakers.resolution.json. Also the manual operations (rename, merge, reassign),
/// which are persisted and idempotent. Heavy work runs at
/// <see cref="InferencePriority.Background"/> through the inference scheduler.
/// </summary>
public sealed class SpeakerResolutionService(
    ISessionStore sessions,
    ISpeakerResolutionStore state,
    IEchoClassifier echoClassifier,
    IDiarizer diarizer,
    IInferenceScheduler scheduler,
    IEnergyEnvelopeReader? energyReader = null,
    SpeakerResolutionOptions? options = null)
{
    private static readonly TranscriptInfo UnknownInfo = new("unknown", "unknown");

    private readonly SpeakerResolutionOptions _options = options ?? new SpeakerResolutionOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SpeakerResolutionOutcome> ResolveAsync(
        string sessionId,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        scheduler.RunAsync(
            InferencePriority.Background,
            ct => Locked(() => ResolveCoreAsync(sessionId, progress, ct), ct),
            cancellationToken);

    /// <summary>
    /// Speakers of a session with colour slots and embeddings. Sessions never resolved
    /// (v1) list the ids found in the transcript ("me", "them").
    /// </summary>
    public async Task<IReadOnlyList<SpeakerProfile>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken)
    {
        var speakers = await sessions.LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var voices = (await state.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Voices.ToDictionary(voice => voice.Id, StringComparer.Ordinal);
        if (speakers.Count == 0)
        {
            var transcript = await sessions.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
            speakers = transcript
                .GroupBy(segment => segment.SpeakerId, StringComparer.Ordinal)
                .Select(group => new Speaker(
                    group.Key,
                    group.Key is SpeakerIds.Me or SpeakerIds.LegacyThem ? SpeakerIds.LegacyTrack(group.Key) : group.First().Track))
                .ToList();
        }

        return speakers.Select(speaker => voices.TryGetValue(speaker.Id, out var voice)
                ? new SpeakerProfile(speaker.Id, speaker.Track, speaker.DisplayName, voice.ColorSlot, voice.Embedding)
                : new SpeakerProfile(speaker.Id, speaker.Track, speaker.DisplayName, SpeakerProfile.DefaultColorSlot(speaker.Id), null))
            .ToList();
    }

    /// <summary>Sets (or clears, with null/blank) the display name. Throws <see cref="ArgumentException"/> for an unknown id.</summary>
    public Task RenameAsync(string sessionId, string speakerId, string? displayName, CancellationToken cancellationToken) =>
        Locked(async () =>
        {
            var profiles = await LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var target = profiles.FirstOrDefault(profile => profile.Id == speakerId)
                ?? throw new ArgumentException($"unknown speaker '{speakerId}'", nameof(speakerId));
            var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            if (target.DisplayName == name)
            {
                return true;
            }

            var renamed = profiles.Select(profile => profile.Id == speakerId ? profile with { DisplayName = name } : profile).ToList();
            await sessions.SaveSpeakersAsync(sessionId, renamed.Select(profile => profile.ToSpeaker()).ToList(), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Moves every segment of <paramref name="mergedId"/> to <paramref name="keptId"/>. A no-op when
    /// <paramref name="mergedId"/> no longer exists; throws when <paramref name="keptId"/> is unknown.
    /// </summary>
    public Task MergeAsync(string sessionId, string keptId, string mergedId, CancellationToken cancellationToken) =>
        Locked(async () =>
        {
            var profiles = await LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (keptId == mergedId || profiles.All(profile => profile.Id != mergedId))
            {
                return true;
            }

            if (profiles.All(profile => profile.Id != keptId))
            {
                throw new ArgumentException($"unknown speaker '{keptId}'", nameof(keptId));
            }

            await ApplyEditAsync(sessionId, profiles, new MergeSpeakers(mergedId, keptId), edits => edits, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Assigns final-transcript segment <paramref name="segmentIndex"/> to <paramref name="speakerId"/>
    /// ("me" or "speaker-N"; a new N creates that speaker). Replaces an earlier reassignment of the same segment.
    /// </summary>
    public Task ReassignAsync(string sessionId, int segmentIndex, string speakerId, CancellationToken cancellationToken) =>
        Locked(async () =>
        {
            if (!SpeakerProfile.IsAssignable(speakerId))
            {
                throw new ArgumentException($"'{speakerId}' is not \"me\" or \"speaker-N\"", nameof(speakerId));
            }

            var transcript = await sessions.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
            if (segmentIndex < 0 || segmentIndex >= transcript.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentIndex), segmentIndex, "no such transcript segment");
            }

            var segment = transcript[segmentIndex];
            var edit = new ReassignSegment(segment.Track, TimeSpan.FromMilliseconds((long)segment.Start.TotalMilliseconds), speakerId);
            var profiles = await LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await ApplyEditAsync(
                sessionId,
                profiles,
                edit,
                edits => edits.Where(existing => existing is not ReassignSegment previous ||
                    previous.Track != edit.Track || previous.Start != edit.Start).ToList(),
                cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task ApplyEditAsync(
        string sessionId,
        IReadOnlyList<SpeakerProfile> profiles,
        SpeakerEdit edit,
        Func<IReadOnlyList<SpeakerEdit>, IReadOnlyList<SpeakerEdit>> previousEdits,
        CancellationToken cancellationToken)
    {
        var session = await sessions.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"unknown session '{sessionId}'", nameof(sessionId));
        var transcript = await sessions.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
        var current = await state.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var (segments, speakers) = SpeakerEdits.Apply(transcript, profiles, [edit]);
        var edits = previousEdits(current.Edits).Append(edit).ToList();
        await SaveAsync(session, segments, speakers, edits, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeakerResolutionOutcome> ResolveCoreAsync(string sessionId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new SpeakerResolutionOutcome(SpeakerResolutionStatus.SessionNotFound);
        }

        var transcript = await sessions.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
        if (transcript.Count == 0)
        {
            return new SpeakerResolutionOutcome(SpeakerResolutionStatus.NoTranscript);
        }

        var micTrack = AudioOf(session, Track.Microphone);
        var systemTrack = AudioOf(session, Track.System);
        var micSegments = transcript.Where(segment => segment.Track == Track.Microphone).ToList();
        var systemSegments = transcript.Where(segment => segment.Track == Track.System).ToList();

        EnergyEnvelope? micEnergy = null, systemEnergy = null;
        if (energyReader is not null && micTrack is not null && systemTrack is not null && micSegments.Count > 0)
        {
            micEnergy = await energyReader.ReadAsync(micTrack.Value.Path, micTrack.Value.Offset, cancellationToken).ConfigureAwait(false);
            systemEnergy = await energyReader.ReadAsync(systemTrack.Value.Path, systemTrack.Value.Offset, cancellationToken).ConfigureAwait(false);
        }

        var verdicts = await echoClassifier.ClassifyAsync(
            new EchoClassificationRequest(micSegments, systemSegments, micEnergy, systemEnergy),
            cancellationToken).ConfigureAwait(false);

        var diarizeMic = _options.DiarizeMicrophone && micTrack is not null && micSegments.Count > 0;
        var systemDiarization = systemTrack is not null && systemSegments.Count > 0
            ? await diarizer.DiarizeAsync(
                new DiarizationRequest(systemTrack.Value.Path, Track.System, systemTrack.Value.Offset),
                Scaled(progress, 0, diarizeMic ? 0.5 : 0.95),
                cancellationToken).ConfigureAwait(false)
            : new DiarizationResult(Track.System, [], new Dictionary<int, float[]>());
        var micDiarization = diarizeMic
            ? await diarizer.DiarizeAsync(
                new DiarizationRequest(micTrack!.Value.Path, Track.Microphone, micTrack.Value.Offset),
                Scaled(progress, 0.5, 0.95),
                cancellationToken).ConfigureAwait(false)
            : null;

        var resolved = SpeakerResolver.Resolve(
            new SpeakerResolutionInput(transcript, verdicts, systemDiarization, micDiarization),
            _options);

        var current = await state.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var (segments, speakers) = SpeakerEdits.Apply(resolved.Segments, resolved.Speakers, current.Edits);

        // Names survive re-resolution: ids are stable for the same audio.
        var names = (await sessions.LoadSpeakersAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Where(speaker => speaker.DisplayName is not null)
            .ToDictionary(speaker => speaker.Id, speaker => speaker.DisplayName, StringComparer.Ordinal);
        speakers = speakers.Select(profile => profile with { DisplayName = names.GetValueOrDefault(profile.Id) }).ToList();

        await SaveAsync(session, segments, speakers, current.Edits, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return new SpeakerResolutionOutcome(
            SpeakerResolutionStatus.Resolved,
            speakers.Count,
            resolved.EchoSegmentsRemoved,
            resolved.EchoSegmentsReattributed);
    }

    private async Task SaveAsync(
        Session session,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerProfile> speakers,
        IReadOnlyList<SpeakerEdit> edits,
        CancellationToken cancellationToken)
    {
        await sessions.SaveTranscriptAsync(session.Id, TranscriptPass.Final, ReadTranscriptInfo(session.Folder), segments, cancellationToken)
            .ConfigureAwait(false);
        await sessions.SaveSpeakersAsync(session.Id, speakers.Select(profile => profile.ToSpeaker()).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var voices = speakers.Select(profile => new SpeakerVoice(profile.Id, profile.ColorSlot, profile.Embedding)).ToList();
        await state.SaveAsync(session.Id, new SpeakerResolutionState(voices, edits), cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (string Path, TimeSpan Offset)? AudioOf(Session session, Track track) =>
        session.Tracks.FirstOrDefault(candidate => candidate.Track == track) is { } found &&
        Path.Combine(session.Folder, found.FileName) is var path && File.Exists(path)
            ? (path, found.StartOffset)
            : null;

    private static IProgress<double>? Scaled(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress(value => progress.Report(from + (to - from) * Math.Clamp(value, 0, 1)));

    /// <summary>
    /// Engine/model of transcript.json, kept when the transcript is rewritten.
    /// ISessionStore does not expose the header yet, so it is read directly.
    /// </summary>
    private static TranscriptInfo ReadTranscriptInfo(string folder)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "transcript.json")));
            var root = document.RootElement;
            return root.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.String &&
                   root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? new TranscriptInfo(engine.GetString()!, model.GetString()!)
                : UnknownInfo;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return UnknownInfo;
        }
    }

    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
