using Quill.Core.Ports;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>
/// Notes tab: notes.md of the meeting. Saved about 800 ms after typing stops, and on
/// <see cref="FlushAsync"/> (switching meeting, hiding the window), so nothing typed is lost.
/// </summary>
internal sealed class NotesViewModel : ObservableObject
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly ISessionStore _store;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string _sessionId = string.Empty;
    private string _text = string.Empty;
    private string _savedText = string.Empty;
    private string _status = string.Empty;
    private bool _isLoaded;
    private CancellationTokenSource? _pendingSave;

    public NotesViewModel(ISessionStore store, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _store = store;
        _delay = delay ?? Task.Delay;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? string.Empty) || !_isLoaded)
            {
                return;
            }

            Raise(nameof(IsDirty));
            ScheduleSave();
        }
    }

    /// <summary>False until the file was read, so an early keystroke cannot overwrite it.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    public bool IsDirty => _isLoaded && !string.Equals(_text, _savedText, StringComparison.Ordinal);

    /// <summary>"Guardado", "Guardando...", or the save error.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public async Task LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        await FlushAsync().ConfigureAwait(true);
        _sessionId = sessionId;
        IsLoaded = false;
        Status = string.Empty;
        string text;
        try
        {
            text = await _store.LoadNotesAsync(sessionId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = Loc.Get("Notes_LoadFailed");
            return;
        }

        if (cancellationToken.IsCancellationRequested || !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        _savedText = text;
        _text = text;
        Raise(nameof(Text));
        IsLoaded = true;
        Raise(nameof(IsDirty));
    }

    /// <summary>Saves pending changes now. Safe to call at any time.</summary>
    public async Task FlushAsync()
    {
        _pendingSave?.Cancel();
        _pendingSave = null;
        await SaveAsync().ConfigureAwait(true);
    }

    private async void ScheduleSave()
    {
        _pendingSave?.Cancel();
        var cancellation = new CancellationTokenSource();
        _pendingSave = cancellation;
        try
        {
            await _delay(SaveDelay, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_pendingSave, cancellation))
        {
            return;
        }

        _pendingSave = null;
        await SaveAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        if (!IsDirty || _sessionId.Length == 0)
        {
            return;
        }

        var text = _text;
        Status = Loc.Get("Notes_Saving");
        try
        {
            await _store.SaveNotesAsync(_sessionId, text, CancellationToken.None).ConfigureAwait(true);
            _savedText = text;
            Status = Loc.Get("Notes_Saved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = Loc.Format("Notes_SaveFailed", exception.Message);
        }

        Raise(nameof(IsDirty));
    }
}
