using Quill.Core.Domain;
using Quill.Core.Storage;
using Xunit;

namespace Quill.Core.Tests.Storage;

public sealed class NotesDocumentTests
{
    [Fact]
    public void RoundTripsTimedAndFreeNotes()
    {
        IReadOnlyList<Note> notes = [new("Revisar precios", TimeSpan.FromSeconds(75)), new("Llamar a Ana")];

        var markdown = NotesDocument.Render(notes);

        Assert.Equal("- [00:01:15] Revisar precios\n- Llamar a Ana\n", markdown);
        Assert.Equal(notes, NotesDocument.Parse(markdown));
    }

    [Fact]
    public void ParsesHandWrittenMarkdownLeniently()
    {
        var notes = NotesDocument.Parse("# Notas\n\n* [1:02:03] uno\nlinea suelta\n-   \n");

        Assert.Equal(
            [new Note("# Notas"), new Note("uno", TimeSpan.FromSeconds(3723)), new Note("linea suelta")],
            notes);
    }
}
