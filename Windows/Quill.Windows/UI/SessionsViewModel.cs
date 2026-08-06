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
    private bool _activeSessionPaused;
    private string _participationText = string.Empty;
    private double _microphoneShare;
    private SessionRowViewModel? _selectedSession;
    private bool _restoringSelection;
    private string? _readerDirectory;
    private string? _pendingSelectionDirectory;
    private int _readerGeneration;
    private string _readerTitle = string.Empty;
    private string _readerStatus = "Selecciona una sesion para leerla aqui.";
    private string _searchQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private string _statusText = string.Empty;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _readerCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshGeneration;
    private bool _disposed;

    public SessionsViewModel(string recordingsRoot, TranscriptionCoordinator transcription, Dispatcher dispatcher)
    {
        _recordingsRoot = recordingsRoot;
        _transcription = transcription;
        _dispatcher = dispatcher;

        RetryCommand = new RelayCommand(directory => Retry((string)directory!));
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

    public ObservableCollection<TimelineSlotViewModel> ParticipationTimeline { get; } = new();

    public bool HasParticipationTimeline => ParticipationTimeline.Count > 0;

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

            var previousSession = _selectedSession;
            if (previousSession is not null)
            {
                previousSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
            }
            _selectedSession = value;
            if (_selectedSession is not null)
            {
                _selectedSession.PropertyChanged += OnSelectedSessionPropertyChanged;
            }
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

    /// <summary>
    /// Reparto del habla de la sesion seleccionada. Se calcula sobre los segmentos
    /// que el lector ya trajo del disco: no cuesta una segunda lectura.
    /// </summary>
    public string ParticipationText
    {
        get => _participationText;
        private set
        {
            if (_participationText == value)
            {
                return;
            }

            _participationText = value;
            Raise();
            Raise(nameof(HasParticipation));
        }
    }

    public bool HasParticipation => _participationText.Length > 0;

    public double MicrophoneShare
    {
        get => _microphoneShare;
        private set
        {
            if (Math.Abs(_microphoneShare - value) < 0.0001d)
            {
                return;
            }

            _microphoneShare = value;
            Raise();
            Raise(nameof(SystemShare));
        }
    }

    public double SystemShare => 100d - _microphoneShare;

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

    public void Start()
    {
        _pump.Start();
        if (IsSearching)
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    public void Stop()
    {
        _pump.Stop();
        _searchDebounce.Stop();
        CancelSearch();
        _readerGeneration++;
        _readerCancellation?.Cancel();
        _readerCancellation = null;
        _refreshGeneration++;
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
    }

    public void SetActiveSession(string? sessionDirectory, bool isPaused = false)
    {
        _activeSessionDirectory = sessionDirectory;
        _activeSessionPaused = isPaused;
        Refresh();
    }

    /// <summary>Reconstruye la lista leyendo el disco. Solo al abrir la ventana o al pulsar refrescar.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.BeginInvoke(Refresh);
            }
            catch (InvalidOperationException)
            {
                // The window can disappear while a recording is stopping.
            }

            return;
        }

        var previous = SelectedSession?.Directory;
        var activeSession = _activeSessionDirectory;
        var generation = ++_refreshGeneration;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation?.Cancel();
        _refreshCancellation = cancellation;
        _ = RefreshAsync(previous, activeSession, generation, cancellation);
    }

    private async Task RefreshAsync(
        string? previous,
        string? activeSession,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            IReadOnlyList<SessionSnapshot> snapshots;
            try
            {
                snapshots = await Task.Run(
                    () => SessionCatalog.Scan(_recordingsRoot, activeSession, cancellation.Token),
                    cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (generation == _refreshGeneration)
                {
                    StatusText = $"No se pudieron leer las sesiones: {exception.Message}";
                }

                return;
            }

            if (_disposed || generation != _refreshGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }

        // Rehacer la lista pasa por SelectedItem = null. Sin esta bandera, el panel
        // de lectura se vaciaria y se recargaria en cada refresco.
        _restoringSelection = true;
        try
        {
            var selectionToRestore = _pendingSelectionDirectory ?? SelectedSession?.Directory ?? previous;
            _pendingSelectionDirectory = null;
            Sessions.Clear();
            foreach (var snapshot in snapshots)
            {
                var row = new SessionRowViewModel(snapshot);
                if (string.Equals(row.Directory, _activeSessionDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    row.SetPaused(_activeSessionPaused);
                }

                Sessions.Add(row);
            }

            SelectedSession = Sessions.FirstOrDefault(row =>
                string.Equals(row.Directory, selectionToRestore, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _restoringSelection = false;
        }

        LoadReader(SelectedSession, force: true);
        Drain();

        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _transcription.StateChanged -= OnUpdate;
        _transcription.ProgressChanged -= OnUpdate;
        _pump.Stop();
        _searchDebounce.Stop();
        CancelSearch();
        _readerCancellation?.Cancel();
        _readerCancellation?.Dispose();
        _readerCancellation = null;
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        var selectedSession = _selectedSession;
        if (selectedSession is not null)
        {
            selectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
        }
        _inFlight.Clear();
    }

    private void OnUpdate(TranscriptionUpdate update) =>
        _inFlight[update.SessionDirectory] = update;

    private void Retry(string directory)
    {
        _transcription.Retry(directory);
        Refresh();
    }

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

            if (update.State is SessionState.Done or SessionState.Failed or SessionState.Disabled)
            {
                _inFlight.TryRemove(row.Directory, out _);
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
        _readerCancellation?.Cancel();
        _readerCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _readerCancellation = cancellation;
        try
        {
            ReaderLines.Clear();
            ParticipationText = string.Empty;
            MicrophoneShare = 0d;
            ParticipationTimeline.Clear();
            Raise(nameof(HasParticipationTimeline));

            if (row is null)
            {
                ReaderTitle = string.Empty;
                ReaderStatus = "Selecciona una sesion para leerla aqui.";
                return;
            }

            ReaderTitle = row.DisplayName;

            if (!row.IsDone &&
                !(row.IsFailed && File.Exists(Path.Combine(row.Directory, "transcript.json"))))
            {
                ReaderStatus = "Esta sesion todavia no tiene transcript.";
                return;
            }

            ReaderStatus = "Cargando...";
            var path = row.Directory;
            ReaderLoad load;
            try
            {
                load = await Task.Run(
                    () => BuildReader(path, row.Duration, cancellation.Token),
                    cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!_disposed && generation == _readerGeneration)
                {
                    ReaderStatus = "No se pudo leer el transcript de esta sesion.";
                }

                return;
            }

            // Otra seleccion gano la carrera mientras se leia el disco.
            if (_disposed || cancellation.IsCancellationRequested || generation != _readerGeneration)
            {
                return;
            }

            foreach (var line in load.Lines)
            {
                ReaderLines.Add(line);
            }

            foreach (var slot in load.Timeline)
            {
                ParticipationTimeline.Add(new TimelineSlotViewModel(slot));
            }

            Raise(nameof(HasParticipationTimeline));

            ReaderStatus = load.Transcript.Status switch
            {
                TranscriptReadStatus.Missing => "No hay transcript para esta sesion.",
                TranscriptReadStatus.Invalid => "No se pudo leer el transcript de esta sesion.",
                TranscriptReadStatus.Empty => "Transcript valido, sin segmentos.",
                _ when ReaderLines.Count == 0 => "Transcript sin lineas visibles.",
                _ => string.Empty,
            };

            UpdateParticipation(load.Transcript.Segments, load.Summary);
        }
        finally
        {
            if (ReferenceEquals(_readerCancellation, cancellation))
            {
                _readerCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void UpdateParticipation(
        IReadOnlyList<TranscriptSegment> segments,
        ParticipationSummary summary)
    {
        if (!summary.HasSpeech)
        {
            MicrophoneShare = 0d;
            ParticipationText = string.Empty;
            return;
        }

        MicrophoneShare = summary.MicrophoneShare * 100d;

        var silent = SessionInsights.SilentTracks(segments);
        var warning = silent.Count switch
        {
            0 => string.Empty,
            _ when silent.Contains(SessionInsights.MicrophoneSpeaker) =>
                "  ·  aviso: el microfono no capto nada",
            _ => "  ·  aviso: el audio del sistema no capto nada",
        };

        ParticipationText =
            $"me {summary.MicrophoneShare:P0} ({TranscriptExport.Clock((long)summary.Microphone.TotalMilliseconds)})" +
            $"   ·   them {summary.SystemShare:P0} ({TranscriptExport.Clock((long)summary.System.TotalMilliseconds)})" +
            warning;
    }

    private static ReaderLoad BuildReader(
        string sessionDirectory,
        TimeSpan sessionDuration,
        CancellationToken cancellationToken)
    {
        var transcript = TranscriptExport.Read(sessionDirectory);
        if (transcript.Status is TranscriptReadStatus.Missing or TranscriptReadStatus.Invalid)
        {
            return new ReaderLoad(
                transcript,
                Array.Empty<TranscriptLineViewModel>(),
                new ParticipationSummary(TimeSpan.Zero, TimeSpan.Zero),
                Array.Empty<TimelineSlot>());
        }

        var lines = transcript.Segments
            .Select(segment =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TranscriptLineViewModel.Segment(segment);
            })
            .Where(line => line.Text.Length > 0)
            .ToList();

        var markers = SessionMetadata.TryRead(sessionDirectory)?.Markers ?? new List<long>();
        var number = 0;
        foreach (var marker in markers.Where(value => value >= 0).OrderBy(value => value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(TranscriptLineViewModel.Marker(marker, ++number));
        }

        // OrderBy es estable: una marca que cae justo en el inicio de un segmento
        // queda detras de el, que es donde se lee mejor.
        return new ReaderLoad(
            transcript,
            lines.OrderBy(line => line.StartMs).ToList(),
            SessionInsights.Summarize(transcript.Segments),
            SessionInsights.Timeline(transcript.Segments, slots: 48, sessionDuration));
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
                var sessions = SessionCatalog.Scan(root, active, token);
                return TranscriptSearch.Search(sessions, query, token).Take(MaxSearchResults + 1).ToList();
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

                var hasMore = task.Result.Count > MaxSearchResults;
                foreach (var hit in task.Result.Take(MaxSearchResults))
                {
                    SearchResults.Add(new SearchHitViewModel(hit));
                }

                SearchStatus = SearchResults.Count switch
                {
                    0 => "sin resultados",
                    1 => "1 resultado",
                    _ when hasMore => $"mas de {MaxSearchResults} resultados",
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
            return;
        }

        _pendingSelectionDirectory = hit.Directory;
        Refresh();
    }

    /// <summary>
    /// Borrar es irreversible y se lleva los WAV originales por delante, asi que
    /// pregunta siempre y nunca toca una sesion con archivos abiertos.
    /// </summary>
    private void Delete(SessionRowViewModel row)
    {
        if (row.IsBusy ||
            _transcription.IsBusy(row.Directory) ||
            string.Equals(row.Directory, _activeSessionDirectory, StringComparison.OrdinalIgnoreCase))
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

        var directory = row.Directory;
        var displayName = row.DisplayName;
        _ = Task.Run(() => _transcription.TryDeleteSession(directory)).ContinueWith(
            task =>
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    StatusText = "No se pudo borrar la sesion.";
                    return;
                }

                var failure = task.Result;
                if (failure is not null)
                {
                    StatusText = $"No se pudo borrar: {failure}";
                    return;
                }

                StatusText = $"{displayName} borrada.";
                _inFlight.TryRemove(directory, out _);
                if (string.Equals(_selectedSession?.Directory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedSession = null;
                }

                Refresh();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
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

    private void OnSelectedSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _selectedSession) && args.PropertyName == nameof(SessionRowViewModel.DisplayName))
        {
            ReaderTitle = _selectedSession?.DisplayName ?? string.Empty;
        }
    }

    private sealed record ReaderLoad(
        TranscriptReadResult Transcript,
        IReadOnlyList<TranscriptLineViewModel> Lines,
        ParticipationSummary Summary,
        IReadOnlyList<TimelineSlot> Timeline);

    private void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusText = "La ruta ya no existe.";
            return;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                StatusText = "No se puede abrir una ruta enlazada.";
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"No se pudo abrir: {exception.Message}";
        }
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
