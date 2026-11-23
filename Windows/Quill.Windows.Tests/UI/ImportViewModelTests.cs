using Quill.Core.Domain;
using Quill.Core.Import;
using Quill.Windows.Import;
using Quill.Windows.UI;
using Xunit;

namespace Quill.Windows.Tests.UI;

internal sealed class FakeRecordingImporter : IRecordingImporter
{
    public static readonly DateTimeOffset Created = new(2026, 11, 20, 9, 30, 0, TimeSpan.FromHours(1));

    public List<ImportPlan> Imported { get; } = new();

    public Func<ImportPlan, CancellationToken, Task<string>> OnImport { get; set; } =
        (plan, _) => Task.FromResult(Path.Combine("C:", "Quill", "2026.11.20-0930"));

    public ImportSource Describe(string path) =>
        path.Contains("missing", StringComparison.Ordinal)
            ? throw new FileNotFoundException("not found", path)
            : new ImportSource(path, Created);

    public Task<string> ImportAsync(ImportPlan plan, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Imported.Add(plan);
        progress?.Report(0.5);
        return OnImport(plan, cancellationToken);
    }
}

public sealed class ImportViewModelTests
{
    private readonly FakeRecordingImporter _importer = new();
    private readonly List<string> _imported = new();

    public ImportViewModelTests() => Samples.UseSpanish();

    private ImportViewModel Create(params string[] paths) => new(paths, _importer, _imported.Add);

    [Fact]
    public void OneFile_OffersWhoSpeaks_AndDefaultsToTheOthers()
    {
        var import = Create(@"C:\rec\Llamada.m4a");

        Assert.True(import.IsSingle);
        Assert.False(import.IsPair);
        Assert.Equal("Llamada.m4a", import.FileName);
        Assert.Equal(ImportRole.Others, import.SelectedRole.Role);
        Assert.Equal(Track.System, Assert.Single(import.Plan!.Tracks).Track);
        Assert.Equal("Título: Llamada", import.TitleText);
    }

    [Fact]
    public void ChoosingMe_MovesTheFileToTheMicrophone()
    {
        var import = Create(@"C:\rec\dictado.wav");

        import.SelectedRole = import.Roles.Single(role => role.Role == ImportRole.Me);

        Assert.Equal(Track.Microphone, Assert.Single(import.Plan!.Tracks).Track);
    }

    [Fact]
    public void TwoFiles_ShowWhichIsTheMicrophone_AndSwapExchangesThem()
    {
        var import = Create(@"C:\rec\a.wav", @"C:\rec\b.wav");

        Assert.True(import.IsPair);
        Assert.Equal("Micrófono (yo): a.wav", import.MicrophoneText);
        Assert.Equal("Sistema (otros): b.wav", import.SystemText);

        import.SwapCommand.Execute(null);

        Assert.Equal("Micrófono (yo): b.wav", import.MicrophoneText);
        Assert.Equal("Sistema (otros): a.wav", import.SystemText);
    }

    [Fact]
    public void UnsupportedOrTooManyFiles_ShowAnError_AndCannotImport()
    {
        var unsupported = Create(@"C:\rec\notas.txt");
        Assert.Equal("Formato no admitido: notas.txt", unsupported.ErrorText);
        Assert.False(unsupported.ImportCommand.CanExecute(null));

        var tooMany = Create(@"C:\a.wav", @"C:\b.wav", @"C:\c.wav");
        Assert.Equal("Elige uno o dos archivos.", tooMany.ErrorText);
        Assert.False(tooMany.HasPlan);

        var missing = Create(@"C:\missing.wav");
        Assert.StartsWith("No se pudo importar", missing.ErrorText);
    }

    [Fact]
    public async Task Import_ConvertsWithThePlan_HandsOverTheSession_AndCloses()
    {
        var import = Create(@"C:\rec\a.wav", @"C:\rec\b.wav");
        var closed = false;
        import.CloseRequested += (_, _) => closed = true;

        import.ImportCommand.Execute(null);
        await WaitUntil(() => closed);

        Assert.Same(import.Plan, Assert.Single(_importer.Imported));
        Assert.Equal(Path.Combine("C:", "Quill", "2026.11.20-0930"), Assert.Single(_imported));
        Assert.False(import.IsImporting);
    }

    [Fact]
    public async Task ImportFailure_StaysOpen_WithTheReason()
    {
        _importer.OnImport = (_, _) => Task.FromException<string>(new InvalidDataException("no audio"));
        var import = Create(@"C:\rec\a.wav");
        var closed = false;
        import.CloseRequested += (_, _) => closed = true;

        import.ImportCommand.Execute(null);
        await WaitUntil(() => import.HasError);

        Assert.Equal("No se pudo importar: no audio", import.ErrorText);
        Assert.False(closed);
        Assert.Empty(_imported);
        Assert.True(import.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_WhileConverting_StopsTheImport_AndCloses()
    {
        var started = new TaskCompletionSource();
        _importer.OnImport = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        };
        var import = Create(@"C:\rec\a.wav");
        var closed = false;
        import.CloseRequested += (_, _) => closed = true;

        import.ImportCommand.Execute(null);
        await started.Task;
        Assert.True(import.IsImporting);
        import.CancelCommand.Execute(null);
        await WaitUntil(() => closed);

        Assert.Empty(_imported);
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
