using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;

namespace Quill.Windows.UI;

internal sealed class SessionsViewModel : IDisposable
{
    private readonly string _recordingsRoot;
    private readonly TranscriptionCoordinator _transcription;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pump;
    private readonly ConcurrentDictionary<string, TranscriptionUpdate> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSessionDirectory;

    public SessionsViewModel(string recordingsRoot, TranscriptionCoordinator transcription, Dispatcher dispatcher)
    {
        _recordingsRoot = recordingsRoot;
        _transcription = transcription;
        _dispatcher = dispatcher;

        RetryCommand = new RelayCommand(directory => _transcription.Retry((string)directory!));
        OpenFolderCommand = new RelayCommand(directory => Open((string)directory!));
        OpenTranscriptCommand = new RelayCommand(directory => Open(Path.Combine((string)directory!, "transcript.md")));
        OpenLogCommand = new RelayCommand(directory => Open(Path.Combine((string)directory!, "transcribe.log")));

        // El coordinador publica sin restriccion. Acumulamos aqui y volcamos a la
        // vista a ritmo fijo: marshalar cada segmento al dispatcher la atascaria.
        _pump = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pump.Tick += (_, _) => Drain();

        _transcription.StateChanged += OnUpdate;
        _transcription.ProgressChanged += OnUpdate;
    }

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    public ICommand RetryCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand OpenTranscriptCommand { get; }

    public ICommand OpenLogCommand { get; }

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

        Sessions.Clear();
        foreach (var snapshot in SessionCatalog.Scan(_recordingsRoot, _activeSessionDirectory))
        {
            Sessions.Add(new SessionRowViewModel(snapshot));
        }

        Drain();
    }

    public void Dispose()
    {
        _transcription.StateChanged -= OnUpdate;
        _transcription.ProgressChanged -= OnUpdate;
        _pump.Stop();
    }

    private void OnUpdate(TranscriptionUpdate update) =>
        _inFlight[update.SessionDirectory] = update;

    private void Drain()
    {
        foreach (var row in Sessions)
        {
            if (_inFlight.TryGetValue(row.Directory, out var update))
            {
                row.Apply(update);
            }
        }
    }

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
