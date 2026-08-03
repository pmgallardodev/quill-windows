using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;

namespace Quill.Windows.UI;

internal sealed class SessionsViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Suficientes para encontrar lo que se busca sin llenar la lista de ruido.</summary>
    private const int MaxSearchResults = 300;

    private readonly string _recordingsRoot;
    private readonly TranscriptionCoordinator _transcription;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _searchDebounce;
    private readonly ConcurrentDictionary<string, TranscriptionUpdate> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private string? _activeSessionDirectory;
    private SessionRowViewModel? _selectedSession;
    private bool _restoringSelection;
    private string? _readerDirectory;
    private int _readerGeneration;
    private string _readerTitle = string.Empty;
    private string _readerStatus = "Selecciona una sesion para leerla aqui.";
    private string _searchQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private string _statusText = string.Empty;
    private CancellationTokenSource? _searchCancellation;

    public SessionsViewModel(string recordingsRoot, TranscriptionCoordinator transcription, Dispatcher dispatcher)
    {
        _recordingsRoot = recordingsRoot;
        _transcription = transcription;
        _dispatcher = dispatcher;

        RetryCommand = new RelayCommand(directory => _transcription.Retry((string)directory!));
        OpenFolderCommand = new RelayCommand(directory => Open((string)directory!));
        OpenTranscriptCommand = new RelayCommand(directory => Open(Path.Combine((string)directory!, "transcript.md")));
        OpenLogCommand = new RelayCommand(directory => Open(Path.Combine((string)directory!, "transcribe.log")));
        RenameCommand = new RelayCommand(row => ((SessionRowViewModel)row!).BeginRename());
        DeleteCommand = new RelayCommand(row => Delete((SessionRowViewModel)row!));
        CopyCommand = new RelayCommand(row => Copy((SessionRowViewModel)row!));
        ExportSrtCommand = new RelayCommand(row => Export((SessionRowViewModel)row!, srt: true));
        ExportVttCommand = new RelayCommand(row => Export((SessionRowViewModel)row!, srt: false));
        OpenHitCommand = new RelayCommand(hit => OpenHit((SearchHitViewModel)hit!));
        ClearSearchCommand = new RelayCommand(_ => SearchQuery = string.Empty);

        // El coordinador publica sin restriccion. Acumulamos aqui y volcamos a la
        // vista a ritmo fijo: marshalar cada segmento al dispatcher la atascaria.
        _pump = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pump.Tick += (_, _) => Drain();

        // Buscar en veinte transcripts cuesta lo suyo: no en cada tecla.
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _searchDebounce.Tick += (_, _) => RunSearch();

        _transcription.StateChanged += OnUpdate;
        _transcription.ProgressChanged += OnUpdate;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    /// <summary>Segmentos y marcas de la sesion seleccionada, en orden temporal.</summary>
    public ObservableCollection<TranscriptLineViewModel> ReaderLines { get; } = new();

    public ObservableCollection<SearchHitViewModel> SearchResults { get; } = new();

    public ICommand RetryCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand OpenTranscriptCommand { get; }

    public ICommand OpenLogCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand ExportSrtCommand { get; }

    public ICommand ExportVttCommand { get; }

    public ICommand OpenHitCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public SessionRowViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (ReferenceEquals(_selectedSession, value))
            {
                return;
            }

            _selectedSession = value;
            Raise();

            if (!_restoringSelection)
            {
                LoadReader(value);
            }
        }
    }

    public string ReaderTitle
    {
        get => _readerTitle;
        private set
        {
            _readerTitle = value;
            Raise();
        }
    }

    /// <summary>Lo que ocupa el panel cuando no hay lineas que mostrar.</summary>
    public string ReaderStatus
    {
        get => _readerStatus;
        private set
        {
            _readerStatus = value;
            Raise();
            Raise(nameof(HasReaderStatus));
        }
    }

    public bool HasReaderStatus => ReaderStatus.Length > 0;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value)
            {
                return;
            }

            _searchQuery = value;
            Raise();
            Raise(nameof(IsSearching));

            _searchDebounce.Stop();
            if (IsSearching)
            {
                SearchStatus = "buscando...";
                _searchDebounce.Start();
            }
            else
            {
                CancelSearch();
                SearchResults.Clear();
                SearchStatus = string.Empty;
            }
        }
    }

    /// <summary>Con busqueda activa el panel derecho muestra resultados en vez del transcript.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchQuery);

    public string SearchStatus
    {
        get => _searchStatus;
        private set
        {
            _searchStatus = value;
            Raise();
        }
    }

    /// <summary>Ultimo resultado de una accion: exportar, copiar o borrar.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            Raise();
        }
    }

    public void Start() => _pump.Start();

    public void Stop() => _pump.Stop();

    public void SetActiveSession(string? sessionDirectory)
    {
        _activeSessionDirectory = sessionDirectory;
        Refresh();
    }

    /// <summary>Reconstruye la lista leyendo el disco. Solo al abrir la ventana o al pulsar refrescar.</summary>
    public void Refresh()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Refresh);
            return;
        }

        var previous = SelectedSession?.Directory;

        // Rehacer la lista pasa por SelectedItem = null. Sin esta bandera, el panel
        // de lectura se vaciaria y se recargaria en cada refresco.
        _restoringSelection = true;
        try
        {
            Sessions.Clear();
            foreach (var snapshot in SessionCatalog.Scan(_recordingsRoot, _activeSessionDirectory))
            {
                Sessions.Add(new SessionRowViewModel(snapshot));
            }

            SelectedSession = Sessions.FirstOrDefault(row =>
                string.Equals(row.Directory, previous, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _restoringSelection = false;
        }

        LoadReader(SelectedSession);
        Drain();
    }

    public void Dispose()
    {
        _transcription.StateChanged -= OnUpdate;
        _transcription.ProgressChanged -= OnUpdate;
        _pump.Stop();
        _searchDebounce.Stop();
        CancelSearch();
    }

    private void OnUpdate(TranscriptionUpdate update) =>
        _inFlight[update.SessionDirectory] = update;

    private void Drain()
    {
        foreach (var row in Sessions)
        {
            if (!_inFlight.TryGetValue(row.Directory, out var update))
            {
                continue;
            }

            var wasDone = row.IsDone;
            row.Apply(update);

            // La sesion que se esta leyendo acaba de terminar: ahora si hay texto.
            if (!wasDone && row.IsDone && ReferenceEquals(row, _selectedSession))
            {
                LoadReader(row, force: true);
            }
        }
    }

    /// <summary>
    /// Leer el transcript va fuera del hilo de UI: son miles de segmentos y la
    /// ventana no puede congelarse al cambiar de sesion. Cada carga lleva su numero
    /// de generacion, asi que una lectura lenta ya obsoleta se descarta al volver.
    /// </summary>
    private async void LoadReader(SessionRowViewModel? row, bool force = false)
    {
        var directory = row?.Directory;
        if (!force && string.Equals(directory, _readerDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _readerDirectory = directory;
        var generation = ++_readerGeneration;
        ReaderLines.Clear();

        if (row is null)
        {
            ReaderTitle = string.Empty;
            ReaderStatus = "Selecciona una sesion para leerla aqui.";
            return;
        }

        ReaderTitle = row.DisplayName;

        if (!row.IsDone)
        {
            ReaderStatus = "Esta sesion todavia no tiene transcript.";
            return;
        }

        ReaderStatus = "Cargando...";
        var path = row.Directory;
        var lines = await Task.Run(() => BuildLines(path)).ConfigureAwait(true);

        // Otra seleccion gano la carrera mientras se leia el disco.
        if (generation != _readerGeneration)
        {
            return;
        }

        foreach (var line in lines)
        {
            ReaderLines.Add(line);
        }

        ReaderStatus = ReaderLines.Count == 0
            ? "No se pudo leer el transcript de esta sesion."
            : string.Empty;
    }

    private static IReadOnlyList<TranscriptLineViewModel> BuildLines(string sessionDirectory)
    {
        var lines = TranscriptExport.TryReadSegments(sessionDirectory)
            .Select(TranscriptLineViewModel.Segment)
            .ToList();

        var markers = SessionMetadata.TryRead(sessionDirectory)?.Markers ?? new List<long>();
        var number = 0;
        foreach (var marker in markers.Where(value => value >= 0).OrderBy(value => value))
        {
            lines.Add(TranscriptLineViewModel.Marker(marker, ++number));
        }

        // OrderBy es estable: una marca que cae justo en el inicio de un segmento
        // queda detras de el, que es donde se lee mejor.
        return lines.OrderBy(line => line.StartMs).ToList();
    }

    private void RunSearch()
    {
        _searchDebounce.Stop();
        CancelSearch();

        var query = _searchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var root = _recordingsRoot;
        var active = _activeSessionDirectory;

        _ = Task.Run(
            () =>
            {
                var sessions = SessionCatalog.Scan(root, active);
                return TranscriptSearch.Search(sessions, query, token).Take(MaxSearchResults).ToList();
            },
            token).ContinueWith(
            task =>
            {
                if (!ReferenceEquals(_searchCancellation, cancellation))
                {
                    return;
                }

                SearchResults.Clear();

                if (task.Status != TaskStatus.RanToCompletion)
                {
                    SearchStatus = "no se pudo completar la busqueda";
                    return;
                }

                foreach (var hit in task.Result)
                {
                    SearchResults.Add(new SearchHitViewModel(hit));
                }

                SearchStatus = SearchResults.Count switch
                {
                    0 => "sin resultados",
                    1 => "1 resultado",
                    MaxSearchResults => $"mas de {MaxSearchResults} resultados",
                    var count => $"{count} resultados",
                };
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void CancelSearch()
    {
        var previous = _searchCancellation;
        _searchCancellation = null;
        if (previous is null)
        {
            return;
        }

        previous.Cancel();
        previous.Dispose();
    }

    private void OpenHit(SearchHitViewModel hit)
    {
        var target = Sessions.FirstOrDefault(row =>
            string.Equals(row.Directory, hit.Directory, StringComparison.OrdinalIgnoreCase));

        SearchQuery = string.Empty;
        if (target is not null)
        {
            SelectedSession = target;
        }
    }

    /// <summary>
    /// Borrar es irreversible y se lleva los WAV originales por delante, asi que
    /// pregunta siempre y nunca toca una sesion con archivos abiertos.
    /// </summary>
    private void Delete(SessionRowViewModel row)
    {
        if (row.IsBusy)
        {
            StatusText = "No se puede borrar una sesion en curso.";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"Se borrara la carpeta de {row.DisplayName} con el audio y el transcript.\n\nEsto no se puede deshacer.",
            "Borrar sesion",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);

        if (answer != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        var failure = SessionCatalog.TryDelete(row.Directory);
        StatusText = failure is null
            ? $"{row.DisplayName} borrada."
            : $"No se pudo borrar: {failure}";

        if (ReferenceEquals(row, _selectedSession))
        {
            SelectedSession = null;
        }

        Refresh();
    }

    private void Copy(SessionRowViewModel row)
    {
        var path = row.Directory;
        _ = Task.Run(() => TranscriptExport.ToPlainText(TranscriptExport.TryReadSegments(path))).ContinueWith(
            task =>
            {
                var text = task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;
                if (string.IsNullOrEmpty(text))
                {
                    StatusText = "No hay transcript que copiar.";
                    return;
                }

                try
                {
                    // El portapapeles es un recurso de todo el escritorio: cualquier
                    // proceso puede tenerlo bloqueado justo en este instante.
                    System.Windows.Clipboard.SetText(text);
                    StatusText = $"{row.DisplayName} copiada al portapapeles.";
                }
                catch (Exception exception) when (exception is ExternalException or OutOfMemoryException)
                {
                    StatusText = "No se pudo copiar: el portapapeles esta ocupado.";
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Export(SessionRowViewModel row, bool srt)
    {
        var path = row.Directory;
        var fileName = srt ? TranscriptExport.SrtFileName : TranscriptExport.VttFileName;

        _ = Task.Run(() =>
        {
            var segments = TranscriptExport.TryReadSegments(path);
            if (segments.Count == 0)
            {
                return "No hay transcript que exportar.";
            }

            try
            {
                AtomicFile.Write(
                    Path.Combine(path, fileName),
                    srt ? TranscriptExport.ToSrt(segments) : TranscriptExport.ToVtt(segments));
                return $"{fileName} guardado en la carpeta de la sesion.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"No se pudo escribir {fileName}: {exception.Message}";
            }
        }).ContinueWith(
            task => StatusText = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : $"No se pudo escribir {fileName}.",
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
