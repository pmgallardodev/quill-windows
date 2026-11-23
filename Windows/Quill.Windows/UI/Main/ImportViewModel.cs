using System.Windows.Input;
using Quill.Core.Domain;
using Quill.Core.Import;
using Quill.Windows.Import;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>One choice of "who speaks in the recording" for a single imported file.</summary>
internal sealed record ImportRoleOption(ImportRole Role, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Importar grabación" dialog: shows how the chosen files will become a meeting (who is
/// on which track, title, start), lets the user correct it, converts the audio and hands
/// the new session to <c>onImported</c> (which enqueues it for transcription).
/// </summary>
internal sealed class ImportViewModel : ObservableObject
{
    private readonly IRecordingImporter _importer;
    private readonly Action<string> _onImported;
    private readonly IReadOnlyList<ImportSource> _sources;
    private ImportRoleOption _selectedRole;
    private bool _swapped;
    private ImportPlan? _plan;
    private string _errorText = string.Empty;
    private bool _isImporting;
    private double _progress;
    private CancellationTokenSource? _cancellation;

    public ImportViewModel(IReadOnlyList<string> paths, IRecordingImporter importer, Action<string> onImported)
    {
        _importer = importer;
        _onImported = onImported;
        Roles = new[]
        {
            new ImportRoleOption(ImportRole.Others, Loc.Get("Import_RoleOthers")),
            new ImportRoleOption(ImportRole.Me, Loc.Get("Import_RoleMe")),
            new ImportRoleOption(ImportRole.Mixed, Loc.Get("Import_RoleMixed")),
        };
        _selectedRole = Roles[0];
        SwapCommand = new RelayCommand(_ => Swap(), _ => IsPair && !_isImporting);
        ImportCommand = new RelayCommand(_ => _ = ImportAsync(), _ => _plan is not null && !_isImporting);
        CancelCommand = new RelayCommand(_ => Cancel());

        _sources = Describe(paths ?? Array.Empty<string>());
        Rebuild();
    }

    /// <summary>The dialog closes on this: import finished, cancelled, or dismissed.</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<ImportRoleOption> Roles { get; }

    public ImportRoleOption SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (value is not null && Set(ref _selectedRole, value))
            {
                Rebuild();
            }
        }
    }

    public ICommand SwapCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand CancelCommand { get; }

    public bool HasPlan => _plan is not null;

    public bool IsSingle => _plan is not null && _plan.Mode != ImportMode.Pair;

    public bool IsPair => _plan?.Mode == ImportMode.Pair;

    /// <summary>The file of a single-file import.</summary>
    public string FileName => IsSingle ? Path.GetFileName(_plan!.Tracks[0].SourcePath) : string.Empty;

    public string MicrophoneText => Loc.Format("Import_Microphone", FileOf(Track.Microphone));

    public string SystemText => Loc.Format("Import_System", FileOf(Track.System));

    public string TitleText => _plan is null ? string.Empty : Loc.Format("Import_MeetingTitle", _plan.Title ?? string.Empty);

    public string StartedText => _plan is null ? string.Empty : Loc.Format("Import_Started", UiFormat.Date(_plan.Started, "Format_MeetingDate"));

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText.Length > 0;

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (Set(ref _isImporting, value))
            {
                Raise(nameof(ProgressText));
            }
        }
    }

    /// <summary>0..100 while converting.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                Raise(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => _isImporting ? Loc.Format("Import_Converting", (int)_progress) : string.Empty;

    /// <summary>Exposed for tests: the plan the import button will follow.</summary>
    internal ImportPlan? Plan => _plan;

    private IReadOnlyList<ImportSource> Describe(IReadOnlyList<string> paths)
    {
        var sources = new List<ImportSource>(paths.Count);
        foreach (var path in paths)
        {
            if (!ImportPlan.IsSupported(path))
            {
                ErrorText = Loc.Format("Import_Unsupported", Path.GetFileName(path));
                return Array.Empty<ImportSource>();
            }

            try
            {
                sources.Add(_importer.Describe(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ErrorText = Loc.Format("Import_Failed", exception.Message);
                return Array.Empty<ImportSource>();
            }
        }

        if (sources.Count is < 1 or > 2)
        {
            ErrorText = Loc.Get("Import_FileCount");
            return Array.Empty<ImportSource>();
        }

        return sources;
    }

    private void Rebuild()
    {
        if (_sources.Count == 0)
        {
            _plan = null;
        }
        else
        {
            try
            {
                _plan = ImportPlan.Create(_sources, _selectedRole.Role, _swapped);
                ErrorText = string.Empty;
            }
            catch (ArgumentException)
            {
                _plan = null;
                ErrorText = Loc.Get("Import_SameFile");
            }
        }

        Raise(nameof(HasPlan), nameof(IsSingle), nameof(IsPair), nameof(FileName), nameof(MicrophoneText),
            nameof(SystemText), nameof(TitleText), nameof(StartedText));
    }

    private void Swap()
    {
        _swapped = !_swapped;
        Rebuild();
    }

    private async Task ImportAsync()
    {
        if (_plan is not { } plan || _isImporting)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        ErrorText = string.Empty;
        Progress = 0d;
        IsImporting = true;
        try
        {
            var progress = new Progress<double>(fraction => Progress = Math.Round(Math.Clamp(fraction, 0d, 1d) * 100d));
            var directory = await _importer.ImportAsync(plan, progress, cancellation.Token).ConfigureAwait(true);
            IsImporting = false;
            _onImported(directory);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            IsImporting = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IsImporting = false;
            ErrorText = Loc.Format("Import_Failed", exception.Message);
        }
        finally
        {
            _cancellation = null;
            cancellation.Dispose();
        }
    }

    private void Cancel()
    {
        if (_isImporting)
        {
            _cancellation?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private string FileOf(Track track) =>
        _plan?.Tracks.FirstOrDefault(item => item.Track == track) is { } item
            ? Path.GetFileName(item.SourcePath)
            : string.Empty;
}
