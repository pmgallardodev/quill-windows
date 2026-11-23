using Quill.Core.Domain;
using Quill.Windows.UI;
using Xunit;
using LegacySegment = Quill.Windows.Sessions.TranscriptSegment;
using TranscriptSegment = Quill.Core.Domain.TranscriptSegment;

namespace Quill.Windows.Tests.UI;

public sealed class TranscriptEditingTests
{
    private readonly FakeSessionStore _store = new();
    private readonly FakeSessionActions _actions = new();

    public TranscriptEditingTests()
    {
        Samples.UseSpanish();
        _store.Transcripts[Samples.SessionId] = new[]
        {
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Hola a todos", "me", Track.Microphone),
            new TranscriptSegment(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9), "El presupesto", "speaker-1", Track.System),
        };
    }

    private TranscriptViewModel Loaded(Func<Task<string?>>? resummarize = null)
    {
        var transcript = new TranscriptViewModel(_actions, store: _store) { ResummarizeHandler = resummarize ?? (() => Task.FromResult<string?>(null)) };
        var entries = TranscriptViewModel.BuildEntries(
            new[]
            {
                new LegacySegment("me", 0, 4_000, "Hola a todos"),
                new LegacySegment("speaker-1", 6_000, 9_000, "El presupesto"),
            },
            new long[] { 5_000 },
            new SpeakerLabels(new[] { new Speaker("speaker-1", Track.System, "Ana") }));
        transcript.Load(Samples.SessionId, entries, "vacío");
        return transcript;
    }

    private static TranscriptEntryViewModel Line(TranscriptViewModel transcript, string text) =>
        transcript.Entries.Single(entry => entry.Text == text);

    [Fact]
    public void Normalize_TrimsAndFoldsLines_AndRejectsBlank()
    {
        Assert.Equal("uno dos", TranscriptEdits.Normalize("  uno\r\n dos \n"));
        Assert.Null(TranscriptEdits.Normalize(" \n "));
        Assert.Null(TranscriptEdits.Normalize(null));
    }

    [Fact]
    public void Apply_ReplacesOnlyThatSegment_AndMarksItEdited()
    {
        var segments = _store.Transcripts[Samples.SessionId];

        var updated = TranscriptEdits.Apply(segments, 1, TimeSpan.FromSeconds(6), "El presupuesto");

        Assert.Equal("El presupuesto", updated[1].Text);
        Assert.True(updated[1].Edited);
        Assert.Same(segments[0], updated[0]);
        Assert.Throws<InvalidOperationException>(() => TranscriptEdits.Apply(segments, 1, TimeSpan.FromSeconds(7), "x"));
        Assert.Throws<InvalidOperationException>(() => TranscriptEdits.Apply(segments, 5, TimeSpan.Zero, "x"));
    }

    [Fact]
    public void Markers_AreNotEditable_SegmentsKeepTheirIndex()
    {
        var transcript = Loaded();

        Assert.Equal(new[] { 0, -1, 1 }, transcript.Entries.Select(entry => entry.SegmentIndex));
        Assert.False(transcript.BeginEditCommand.CanExecute(transcript.Entries[1]));
        Assert.True(transcript.BeginEditCommand.CanExecute(transcript.Entries[2]));
    }

    [Fact]
    public async Task Save_PersistsTheCorrection_MarksTheLine_AndOffersResummarize()
    {
        var transcript = Loaded();
        var line = Line(transcript, "El presupesto");

        transcript.BeginEditCommand.Execute(line);
        Assert.True(line.IsEditing);
        Assert.Equal("El presupesto", line.EditText);
        line.EditText = "El presupuesto ";
        transcript.SaveEditCommand.Execute(line);
        await WaitUntil(() => !transcript.IsBusy && !line.IsEditing);

        var saved = Assert.Single(_store.SavedTranscripts);
        Assert.Equal("El presupuesto", saved.Segments[1].Text);
        Assert.True(saved.Segments[1].Edited);
        Assert.False(saved.Segments[0].Edited);
        Assert.Equal("El presupuesto", line.Text);
        Assert.True(line.IsEdited);
        Assert.True(transcript.HasPendingEdits);
        Assert.True(transcript.CanResummarize);
    }

    [Fact]
    public void Cancel_KeepsTheTextAndSavesNothing()
    {
        var transcript = Loaded();
        var line = Line(transcript, "Hola a todos");

        transcript.BeginEditCommand.Execute(line);
        line.EditText = "Adiós";
        transcript.CancelEditCommand.Execute(null);

        Assert.False(line.IsEditing);
        Assert.Equal("Hola a todos", line.Text);
        Assert.Empty(_store.SavedTranscripts);
        Assert.False(transcript.HasPendingEdits);
    }

    [Fact]
    public async Task SaveWithoutChanges_JustClosesTheEditor()
    {
        var transcript = Loaded();
        var line = Line(transcript, "Hola a todos");

        transcript.BeginEditCommand.Execute(line);
        transcript.SaveEditCommand.Execute(line);
        await Task.Yield();

        Assert.False(line.IsEditing);
        Assert.Empty(_store.SavedTranscripts);
    }

    [Fact]
    public async Task SaveFailure_KeepsTheEditorOpen_WithTheReason()
    {
        _store.FailSaves = true;
        var transcript = Loaded();
        var line = Line(transcript, "Hola a todos");

        transcript.BeginEditCommand.Execute(line);
        line.EditText = "Hola";
        transcript.SaveEditCommand.Execute(line);
        await WaitUntil(() => transcript.HasEditStatus);

        Assert.Equal("No se pudo guardar la corrección: disk full", transcript.EditStatus);
        Assert.True(line.IsEditing);
        Assert.Equal("Hola a todos", line.Text);
    }

    [Fact]
    public async Task Resummarize_RunsTheHandler_AndClearsThePendingEdits()
    {
        var calls = 0;
        var transcript = Loaded(() =>
        {
            calls++;
            return Task.FromResult<string?>(null);
        });
        var line = Line(transcript, "Hola a todos");
        transcript.BeginEditCommand.Execute(line);
        line.EditText = "Hola";
        transcript.SaveEditCommand.Execute(line);
        await WaitUntil(() => transcript.CanResummarize && !transcript.IsBusy);

        transcript.ResummarizeCommand.Execute(null);
        await WaitUntil(() => !transcript.IsBusy);

        Assert.Equal(1, calls);
        Assert.False(transcript.HasPendingEdits);
        Assert.Equal("Resumen actualizado.", transcript.EditStatus);
    }

    [Fact]
    public async Task ResummarizeFailure_KeepsTheOffer()
    {
        var transcript = Loaded(() => Task.FromResult<string?>("sin memoria"));
        var line = Line(transcript, "Hola a todos");
        transcript.BeginEditCommand.Execute(line);
        line.EditText = "Hola";
        transcript.SaveEditCommand.Execute(line);
        await WaitUntil(() => transcript.CanResummarize && !transcript.IsBusy);

        transcript.ResummarizeCommand.Execute(null);
        await WaitUntil(() => !transcript.IsBusy);

        Assert.True(transcript.HasPendingEdits);
        Assert.Equal("No se pudo volver a resumir: sin memoria", transcript.EditStatus);
    }

    [Fact]
    public void WithoutTheAiPack_ResummarizeIsNotOffered()
    {
        _actions.IsAiAvailable = false;
        var transcript = Loaded();

        Assert.False(transcript.CanResummarize);
        Assert.False(transcript.ResummarizeCommand.CanExecute(null));
    }

    [Fact]
    public void SpeakerReassignment_IsHidden_WhenNotAvailable()
    {
        var transcript = Loaded();

        Assert.All(transcript.Entries, entry => Assert.False(entry.CanReassign));
    }

    [Fact]
    public async Task SpeakerReassignment_OffersTheOtherSpeakers_AndCallsTheAction()
    {
        _actions.CanReassignSpeakers = true;
        var reloads = 0;
        var transcript = Loaded();
        transcript.ReloadHandler = () =>
        {
            reloads++;
            return Task.CompletedTask;
        };
        var line = Line(transcript, "El presupesto");

        Assert.Equal(new[] { "Yo" }, line.ReassignOptions.Select(option => option.Label));
        Assert.Equal(new[] { "Ana" }, Line(transcript, "Hola a todos").ReassignOptions.Select(option => option.Label));

        line.ReassignOptions[0].Command.Execute(null);
        await WaitUntil(() => reloads == 1);

        Assert.Equal((Samples.SessionId, TimeSpan.FromSeconds(6), "me"), Assert.Single(_actions.Reassigned));
        Assert.True(transcript.HasPendingEdits);
    }

    [Fact]
    public void SpeakerChoices_PutMeFirst_EvenWhenMeNeverSpoke()
    {
        var entries = new[]
        {
            new TranscriptEntryViewModel(TimeSpan.Zero, "speaker-2", "Interlocutor 2", "a", isMarker: false, segmentIndex: 0),
            new TranscriptEntryViewModel(TimeSpan.FromSeconds(1), "speaker-1", "Ana", "b", isMarker: false, segmentIndex: 1),
            new TranscriptEntryViewModel(TimeSpan.FromSeconds(2), "speaker-2", "Interlocutor 2", "c", isMarker: false, segmentIndex: 2),
        };

        Assert.Equal(
            new[] { "me", "speaker-2", "speaker-1" },
            TranscriptEdits.SpeakerChoices(entries).Select(choice => choice.Id));
    }

    [Fact]
    public void ApplyEditedFlags_MarksStoredCorrections()
    {
        var transcript = Loaded();
        var stored = _store.Transcripts[Samples.SessionId].ToList();
        stored[1] = stored[1] with { Edited = true };

        transcript.ApplyEditedFlags(stored);

        Assert.True(Line(transcript, "El presupesto").IsEdited);
        Assert.False(Line(transcript, "Hola a todos").IsEdited);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
