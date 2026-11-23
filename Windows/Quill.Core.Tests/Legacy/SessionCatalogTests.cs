using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Core.Tests;

public class SessionCatalogTests
{
    private const string Meta = """
        {
          "started": "2026-08-01T16:00:00+00:00",
          "ended": "2026-08-01T16:42:00+00:00",
          "duration_seconds": 2520,
          "files": { "mic": "mic.wav", "system": "system.wav" },
          "start_offset_ms": { "mic": 0, "system": 120 }
        }
        """;

    private const string EmptyTranscript = """
        {
          "engine": "whisper.cpp",
          "model": "test",
          "created_at": "2026-08-01T16:42:00+00:00",
          "segments": []
        }
        """;

    [Fact]
    public void Scan_ReturnsEmpty_WhenRootDoesNotExist()
    {
        Assert.Empty(SessionCatalog.Scan(Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}"), null));
    }

    [Fact]
    public void Scan_MarksActiveSessionAsRecording_EvenWithoutMetadata()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.03-1420");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, session));

        Assert.Equal(SessionState.Recording, snapshot.State);
    }

    [Fact]
    public void Scan_MarksSessionWithoutMetadataAsAbandoned_WhenItIsNotTheActiveOne()
    {
        // RecordingSession solo escribe meta.json en Stop(). Un cierre brusco deja
        // la carpeta sin metadatos para siempre: no es una grabacion en curso.
        using var temp = new TempDirectory();
        var session = temp.Session("2026.07.31-1130");
        temp.Write(session, ".quill-session", string.Empty);

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, activeSessionDirectory: null));

        Assert.Equal(SessionState.Abandoned, snapshot.State);
    }

    [Fact]
    public void Scan_MarksSessionAsDone_WhenTranscriptExists()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "transcript.json", EmptyTranscript);

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Done, snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(2520), snapshot.Duration);
    }

    [Fact]
    public void Scan_TranscriptWins_OverAStatusThatSaysFailed()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "transcript.json", EmptyTranscript);
        temp.Write(session, "status.json", """{ "state": "failed", "error": "obsoleto" }""");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Done, snapshot.State);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public void Scan_MarksSessionAsQueued_WhenOnlyMetadataExists()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Queued, snapshot.State);
    }

    [Fact]
    public void Scan_ReadsFailedStateWithItsErrorAndModel()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.07.31-1130");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", """
            {
              "state": "failed",
              "model": "ggml-large-v3-q5_0",
              "elapsed_seconds": 12.5,
              "error": "no se encontro el modelo"
            }
            """);

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Failed, snapshot.State);
        Assert.Equal("no se encontro el modelo", snapshot.Error);
        Assert.Equal("ggml-large-v3-q5_0", snapshot.Model);
        Assert.Equal(TimeSpan.FromSeconds(12.5), snapshot.TranscriptionElapsed);
    }

    [Fact]
    public void Scan_FallsBackToQueued_WhenStatusStateIsUnknown()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", """{ "state": "algo-del-futuro" }""");

        Assert.Equal(SessionState.Queued, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_FallsBackToQueued_WhenStatusIsCorrupt()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", "{ roto");

        Assert.Equal(SessionState.Queued, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_TreatsCorruptMetadataAsAbandoned()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", "{ roto");
        temp.Write(session, "mic.wav", string.Empty);

        Assert.Equal(SessionState.Abandoned, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_IgnoresForeignArtifactsWithoutSessionIdentity()
    {
        using var temp = new TempDirectory();
        var directory = temp.Session("not-a-session");
        temp.Write(directory, "status.json", "{ \"state\": \"failed\" }");
        temp.Write(directory, "transcribe.log", "log");
        temp.Write(directory, "transcript.md", "markdown");
        temp.Write(directory, "recording.wav", "wav");

        Assert.Empty(SessionCatalog.Scan(temp.Path, null));
    }

    [Fact]
    public void Scan_RecognizesAudioOnlyDirectoryAsAbandoned()
    {
        using var temp = new TempDirectory();
        var directory = temp.Session("2026.08.02-0930");
        temp.Write(directory, "mic.wav", string.Empty);
        temp.Write(directory, "system.wav", string.Empty);

        Assert.Equal(SessionState.Abandoned, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
        Assert.False(SessionCatalog.IsSafeSessionDirectory(temp.Path, directory));
    }

    [Theory]
    [InlineData(SessionStatus.Queued, "Queued")]
    [InlineData(SessionStatus.Running, "Running")]
    [InlineData(SessionStatus.Disabled, "Disabled")]
    public void Scan_PreservesRecoverableStatusWithInvalidTranscript(string status, string expectedState)
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "transcript.json", "{ roto");
        temp.Write(session, "status.json", $"{{ \"state\": \"{status}\" }}");

        Assert.Equal(
            Enum.Parse<SessionState>(expectedState),
            Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_MarksCorruptTranscriptAsFailed_InsteadOfDone()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "transcript.json", "{ roto");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Failed, snapshot.State);
        Assert.Equal("transcript.json invalido", snapshot.Error);
    }

    [Fact]
    public void Scan_MarksDisabledSessionAsDisabled()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", "{ \"state\": \"disabled\" }");

        Assert.Equal(SessionState.Disabled, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_OrdersNewestFirst()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "2026.07.31-1130", "2026.08.03-1420", "2026.08.01-1600" })
        {
            var session = temp.Session(name);
            temp.Write(session, ".quill-session", string.Empty);
        }

        var names = SessionCatalog.Scan(temp.Path, null).Select(snapshot => snapshot.Name).ToArray();

        Assert.Equal(new[] { "2026.08.03-1420", "2026.08.01-1600", "2026.07.31-1130" }, names);
    }

    [Fact]
    public void TryDelete_RejectsAnUnknownDirectory()
    {
        using var temp = new TempDirectory();
        var directory = temp.Session("not-a-session");
        temp.Write(directory, "status.json", "{ \"state\": \"failed\" }");
        temp.Write(directory, "transcribe.log", "log");
        temp.Write(directory, "transcript.md", "markdown");
        temp.Write(directory, "recording.wav", "wav");
        temp.Write(directory, "important.txt", "keep me");

        var failure = SessionCatalog.TryDelete(temp.Path, directory);

        Assert.NotNull(failure);
        Assert.True(File.Exists(Path.Combine(directory, "important.txt")));
    }

    [Fact]
    public void TryDelete_RejectsAudioOnlyDirectory()
    {
        using var temp = new TempDirectory();
        var directory = temp.Session("abandoned");
        temp.Write(directory, "mic.wav", string.Empty);

        var failure = SessionCatalog.TryDelete(temp.Path, directory);

        Assert.NotNull(failure);
        Assert.True(File.Exists(Path.Combine(directory, "mic.wav")));
    }

    [Fact]
    public void TryDelete_RejectsADirectoryOutsideTheRoot()
    {
        using var temp = new TempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), $"quill-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "important.txt"), "keep me");

            var failure = SessionCatalog.TryDelete(temp.Path, outside);

            Assert.NotNull(failure);
            Assert.True(File.Exists(Path.Combine(outside, "important.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
