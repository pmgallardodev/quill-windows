using System.Diagnostics;

namespace Quill.Windows.UI.Companion;

internal enum NotesSaveState
{
    /// <summary>Everything typed is on disk.</summary>
    Saved,

    /// <summary>Changes are waiting for the debounce delay.</summary>
    Pending,

    Saving,

    /// <summary>The last save failed; the next change (or flush) retries.</summary>
    Failed,
}

/// <summary>
/// Debounced autosave of notes.md: every <see cref="Update"/> restarts the delay, and only the
/// newest text is written. Saves never overlap. <see cref="FlushAsync"/> writes pending changes
/// immediately (panel closing, recording stopped). Thread-safe; <see cref="StateChanged"/> may fire
/// on a background thread.
/// </summary>
internal sealed class NotesAutosaver
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    private readonly Func<string, string, CancellationToken, Task> _save;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private CancellationTokenSource? _debounce;
    private string _latest = string.Empty;
    private string _baseline = string.Empty;
    private long _version;
    private long _savedVersion;
    private NotesSaveState _state = NotesSaveState.Saved;

    public NotesAutosaver(Func<string, CancellationToken, Task> save, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(save);
        _save = (_, text, token) => save(text, token);
        _delay = delay ?? DefaultDelay;
    }

    public NotesAutosaver(Func<string, string, CancellationToken, Task> save, TimeSpan? delay = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _delay = delay ?? DefaultDelay;
    }

    public void SetBaseline(string markdown)
    {
        lock (_lock)
        {
            _baseline = markdown ?? string.Empty;
            _latest = _baseline;
            _savedVersion = _version;
        }
    }

    public event EventHandler<NotesSaveState>? StateChanged;

    public NotesSaveState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Records new text and (re)starts the debounce delay.</summary>
    public void Update(string markdown)
    {
        CancellationTokenSource debounce;
        lock (_lock)
        {
            _latest = markdown ?? string.Empty;
            _version++;
            _debounce?.Cancel();
            _debounce = debounce = new CancellationTokenSource();
        }

        SetState(NotesSaveState.Pending);
        _ = DebounceAsync(debounce.Token);
    }

    /// <summary>Writes pending changes now. Never throws; check <see cref="State"/>.</summary>
    public Task FlushAsync()
    {
        lock (_lock)
        {
            _debounce?.Cancel();
            _debounce = null;
        }

        return SaveLatestAsync();
    }

    private async Task DebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveLatestAsync().ConfigureAwait(false);
    }

    private async Task SaveLatestAsync()
    {
        await _saving.WaitAsync().ConfigureAwait(false);
        try
        {
            string text;
            string baseline;
            long version;
            lock (_lock)
            {
                if (_version == _savedVersion)
                {
                    return;
                }

                text = _latest;
                baseline = _baseline;
                version = _version;
            }

            SetState(NotesSaveState.Saving);
            try
            {
                await _save(baseline, text, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning($"quill: notes not saved: {exception.Message}");
                SetState(NotesSaveState.Failed);
                return;
            }

            bool upToDate;
            lock (_lock)
            {
                _savedVersion = version;
                _baseline = text;
                upToDate = _version == version;
            }

            SetState(upToDate ? NotesSaveState.Saved : NotesSaveState.Pending);
        }
        finally
        {
            _saving.Release();
        }
    }

    private void SetState(NotesSaveState state)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, state);
        }
    }
}
