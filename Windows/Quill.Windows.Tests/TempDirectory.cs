namespace Quill.Windows.Tests;

/// <summary>Carpeta temporal que se borra al final del test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quill-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Session(string name)
    {
        var directory = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Write(string sessionDirectory, string fileName, string contents) =>
        File.WriteAllText(System.IO.Path.Combine(sessionDirectory, fileName), contents);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Un test que deja un handle abierto no debe hacer fallar la suite.
        }
    }
}
