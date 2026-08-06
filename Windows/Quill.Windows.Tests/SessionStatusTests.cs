using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Windows.Tests;

public class SessionStatusTests
{
    [Fact]
    public void WriteThenTryRead_RoundTripsEveryField()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");

        new SessionStatus
        {
            State = SessionStatus.Failed,
            UpdatedAt = new DateTimeOffset(2026, 8, 1, 16, 4, 11, TimeSpan.Zero),
            Model = "ggml-large-v3-q5_0",
            ElapsedSeconds = 187.4,
            Error = "no se encontro el modelo Whisper incluido",
        }.Write(session);

        var status = SessionStatus.TryRead(session);

        Assert.NotNull(status);
        Assert.Equal(SessionStatus.Failed, status!.State);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 16, 4, 11, TimeSpan.Zero), status.UpdatedAt);
        Assert.Equal("ggml-large-v3-q5_0", status.Model);
        Assert.Equal(187.4, status.ElapsedSeconds);
        Assert.Equal("no se encontro el modelo Whisper incluido", status.Error);
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenFileMissing()
    {
        using var temp = new TempDirectory();
        Assert.Null(SessionStatus.TryRead(temp.Session("2026.08.01-1600")));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenJsonIsCorrupt()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", "{ esto no es json");

        Assert.Null(SessionStatus.TryRead(session));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenStateIsMissing()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", """{ "updated_at": "2026-08-01T16:04:11+00:00" }""");

        Assert.Null(SessionStatus.TryRead(session));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenFileIsEmpty()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", string.Empty);

        Assert.Null(SessionStatus.TryRead(session));
    }

    [Fact]
    public void TryRead_KeepsUnknownState_SoTheCatalogCanDecide()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", """{ "state": "algo-del-futuro" }""");

        var status = SessionStatus.TryRead(session);

        Assert.NotNull(status);
        Assert.Equal("algo-del-futuro", status!.State);
    }

    [Fact]
    public void Write_LeavesNoTemporaryFilesBehind()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");

        new SessionStatus { State = SessionStatus.Done, UpdatedAt = DateTimeOffset.UtcNow }.Write(session);

        Assert.Equal(new[] { "status.json" }, Directory.GetFiles(session).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenElapsedIsNotFinite()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", "{ \"state\": \"running\", \"elapsed_seconds\": 1e999 }");

        Assert.Null(SessionStatus.TryRead(session));
    }
}
