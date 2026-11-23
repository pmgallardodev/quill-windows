using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Xunit;

namespace Quill.Core.Tests;

public class ResumePolicyTests
{
    private static SessionStatus With(string state) =>
        new() { State = state, UpdatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void DoesNotEnqueue_WhenMetadataIsMissing()
    {
        // Sesion abandonada: sin meta.json no hay offsets ni nombres de pista.
        Assert.False(ResumePolicy.ShouldEnqueue(hasMetadata: false, hasTranscript: false, status: null));
    }

    [Fact]
    public void DoesNotEnqueue_WhenTranscriptAlreadyExists()
    {
        Assert.False(ResumePolicy.ShouldEnqueue(hasMetadata: true, hasTranscript: true, status: null));
    }

    [Fact]
    public void Enqueues_WhenThereIsNoStatusFile()
    {
        // Sesion anterior a esta version.
        Assert.True(ResumePolicy.ShouldEnqueue(hasMetadata: true, hasTranscript: false, status: null));
    }

    [Fact]
    public void Enqueues_WhenStatusIsRunning()
    {
        // Un "running" huerfano no es un fallo: la app se cerro a mitad.
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Running)));
    }

    [Fact]
    public void Enqueues_WhenStatusIsQueued()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Queued)));
    }

    [Fact]
    public void DoesNotEnqueue_WhenStatusIsFailed()
    {
        // El bucle de reintentos se corta aqui: reintentar es decision del usuario.
        Assert.False(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Failed)));
    }

    [Fact]
    public void Enqueues_WhenStatusIsDoneButTranscriptIsGone()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Done)));
    }

    [Fact]
    public void Enqueues_WhenStatusIsUnknown()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With("algo-del-futuro")));
    }

    [Fact]
    public void Enqueues_WhenTranscriptionWasDisabledAndIsEnabledAgain()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Disabled)));
    }

    [Theory]
    [InlineData(SessionStatus.Queued)]
    [InlineData(SessionStatus.Running)]
    [InlineData(SessionStatus.Disabled)]
    public void Enqueues_WhenTranscriptIsInvalidButStateIsRecoverable(string state)
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, TranscriptReadStatus.Invalid, With(state)));
    }

    [Fact]
    public void DoesNotEnqueue_WhenTranscriptIsInvalidWithoutRecoverableState()
    {
        Assert.False(ResumePolicy.ShouldEnqueue(true, TranscriptReadStatus.Invalid, null));
        Assert.False(ResumePolicy.ShouldEnqueue(true, TranscriptReadStatus.Invalid, With(SessionStatus.Failed)));
        Assert.False(ResumePolicy.ShouldEnqueue(true, TranscriptReadStatus.Invalid, With(SessionStatus.Done)));
    }
}
