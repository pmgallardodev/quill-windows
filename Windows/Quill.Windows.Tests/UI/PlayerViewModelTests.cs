using Quill.Windows.Playback;
using Quill.Windows.UI;
using Xunit;
using LegacySegment = Quill.Windows.Sessions.TranscriptSegment;

namespace Quill.Windows.Tests.UI;

internal sealed class FakeSessionAudio : ISessionAudio
{
    public event EventHandler? Stopped;

    public bool IsPlaying { get; set; }

    public TimeSpan Position { get; set; }

    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);

    public double Speed { get; set; } = 1d;

    public bool Disposed { get; private set; }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Seek(TimeSpan position) => Position = position;

    public void End()
    {
        IsPlaying = false;
        Position = Duration;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSessionAudioSource : ISessionAudioSource
{
    public Dictionary<string, FakeSessionAudio?> Audio { get; } = new();

    public int Opened { get; private set; }

    public Task<ISessionAudio?> OpenAsync(string sessionId, CancellationToken cancellationToken)
    {
        Opened++;
        if (!Audio.TryGetValue(sessionId, out var audio))
        {
            return Task.FromException<ISessionAudio?>(new IOException("locked"));
        }

        return Task.FromResult<ISessionAudio?>(audio);
    }
}

public sealed class PlayerViewModelTests
{
    private readonly FakeSessionAudioSource _source = new();
    private readonly FakeSessionAudio _audio = new();

    public PlayerViewModelTests()
    {
        Samples.UseSpanish();
        _source.Audio[Samples.SessionId] = _audio;
    }

    [Fact]
    public async Task PlayAt_OpensTheAudio_SeeksAndPlays()
    {
        var player = new PlayerViewModel(_source);

        await player.PlayAtAsync(Samples.SessionId, TimeSpan.FromSeconds(65));

        Assert.True(player.IsPlaying);
        Assert.True(_audio.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(65), _audio.Position);
        Assert.Equal(600d, player.DurationSeconds);
        Assert.Equal("1:05 / 10:00", player.TimeText);
    }

    [Fact]
    public async Task PlayPause_Toggles_AndOpensTheAudioOnlyOnce()
    {
        var player = new PlayerViewModel(_source);
        player.SetSession(Samples.SessionId);

        await player.PlayPauseAsync();
        Assert.True(player.IsPlaying);
        await player.PlayPauseAsync();
        Assert.False(player.IsPlaying);
        Assert.False(_audio.IsPlaying);
        await player.PlayPauseAsync();

        Assert.Equal(1, _source.Opened);
    }

    [Fact]
    public async Task DraggingTheSlider_Seeks()
    {
        var player = new PlayerViewModel(_source);
        await player.PlayAtAsync(Samples.SessionId, TimeSpan.Zero);

        player.PositionSeconds = 120;

        Assert.Equal(TimeSpan.FromSeconds(120), _audio.Position);
    }

    [Fact]
    public async Task Speed_IsAppliedToTheAudio()
    {
        var player = new PlayerViewModel(_source);
        await player.PlayAtAsync(Samples.SessionId, TimeSpan.Zero);

        player.SelectedSpeed = player.Speeds.Single(speed => speed.Speed == 1.5d);

        Assert.Equal(1.5d, _audio.Speed);
        Assert.Equal(new[] { "1x", "1,25x", "1,5x", "2x" }, player.Speeds.Select(speed => speed.Label));
    }

    [Fact]
    public async Task EndOfAudio_StopsPlaying()
    {
        var player = new PlayerViewModel(_source);
        await player.PlayAtAsync(Samples.SessionId, TimeSpan.Zero);

        _audio.End();

        Assert.False(player.IsPlaying);
        Assert.Equal(600d, player.PositionSeconds);
    }

    [Fact]
    public async Task MissingAudio_SaysSo_InsteadOfPlaying()
    {
        _source.Audio[Samples.SessionId] = null;
        var player = new PlayerViewModel(_source);

        await player.PlayAtAsync(Samples.SessionId, TimeSpan.FromSeconds(5));

        Assert.False(player.IsPlaying);
        Assert.Equal("El audio de esta reunión no está disponible.", player.Status);
    }

    [Fact]
    public async Task UnreadableAudio_ShowsTheError()
    {
        var player = new PlayerViewModel(_source);

        await player.PlayAtAsync("other-session", TimeSpan.Zero);

        Assert.Equal("No se pudo reproducir el audio: locked", player.Status);
    }

    [Fact]
    public async Task AnotherMeeting_ReleasesTheAudio()
    {
        var player = new PlayerViewModel(_source);
        await player.PlayAtAsync(Samples.SessionId, TimeSpan.FromSeconds(30));

        player.SetSession("another");

        Assert.True(_audio.Disposed);
        Assert.False(player.IsPlaying);
        Assert.Equal(0d, player.PositionSeconds);
    }

    [Fact]
    public async Task TimestampLinks_PlayThroughTheWindowPlayer()
    {
        var player = new PlayerViewModel(_source);
        var actions = new PlaybackSessionActions(new FakeSessionActions(), player);

        actions.PlayAt(Samples.SessionId, TimeSpan.FromSeconds(42));
        await Task.Yield();

        Assert.Equal(TimeSpan.FromSeconds(42), _audio.Position);
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public async Task Transcript_HighlightsTheSegmentBeingHeard()
    {
        var player = new PlayerViewModel(_source);
        player.SetSession(Samples.SessionId);
        var transcript = new TranscriptViewModel(new FakeSessionActions(), player);
        var entries = TranscriptViewModel.BuildEntries(
            new[]
            {
                new LegacySegment("me", 0, 4_000, "Hola"),
                new LegacySegment("speaker-1", 6_000, 9_000, "Buenas"),
                new LegacySegment("me", 30_000, 32_000, "Seguimos"),
            },
            new long[] { 7_000 },
            SpeakerLabels.Empty);
        transcript.Load(Samples.SessionId, entries, "vacío");

        await player.PlayAtAsync(Samples.SessionId, TimeSpan.FromSeconds(7.5));
        Assert.Equal("Buenas", transcript.PlayingEntry?.Text);
        Assert.True(transcript.PlayingEntry!.IsPlaying);

        _audio.Position = TimeSpan.FromSeconds(9.8);
        player.Tick();
        Assert.Equal("Buenas", transcript.PlayingEntry?.Text);

        _audio.Position = TimeSpan.FromSeconds(20);
        player.Tick();
        Assert.Null(transcript.PlayingEntry);
        Assert.All(transcript.Entries, entry => Assert.False(entry.IsPlaying));

        _audio.Position = TimeSpan.FromSeconds(31);
        player.Tick();
        Assert.Equal("Seguimos", transcript.PlayingEntry?.Text);
    }

    [Fact]
    public async Task HidingInvalidatesPendingOpenWithoutDisposingNewerAudio()
    {
        var pending = new TaskCompletionSource<ISessionAudio?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new DelegateAudioSource((_, _) => Interlocked.Increment(ref calls) == 1
            ? pending.Task
            : Task.FromResult<ISessionAudio?>(_audio));
        var player = new PlayerViewModel(source);
        player.SetSession(Samples.SessionId);

        var hiddenOpen = player.PlayPauseAsync();
        player.CancelPendingOpen();
        await player.PlayPauseAsync();
        Assert.True(_audio.IsPlaying);

        pending.SetResult(_audio);
        await hiddenOpen;

        Assert.True(_audio.IsPlaying);
        Assert.False(_audio.Disposed);
        Assert.True(player.IsPlaying);
    }

    private sealed class DelegateAudioSource(Func<string, CancellationToken, Task<ISessionAudio?>> open) : ISessionAudioSource
    {
        public Task<ISessionAudio?> OpenAsync(string sessionId, CancellationToken cancellationToken) => open(sessionId, cancellationToken);
    }
}
