using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>One speaker in the panel: optional name and the "¿Es Juan?" chip.</summary>
internal sealed class SpeakerRowViewModel : ObservableObject
{
    private string _nameDraft;

    public SpeakerRowViewModel(SpeakerPanelItem item, SpeakersPanelViewModel panel)
    {
        SpeakerId = item.SpeakerId;
        DisplayName = item.DisplayName;
        SuggestedName = item.SuggestedName;
        _nameDraft = item.DisplayName ?? string.Empty;
        DefaultLabel = SpeakerLabels.DefaultLabel(item.SpeakerId);
        SaveNameCommand = new RelayCommand(_ => _ = panel.RenameAsync(this), _ => !panel.IsBusy && IsNameChanged);
        ConfirmCommand = new RelayCommand(_ => _ = panel.ConfirmAsync(this), _ => !panel.IsBusy && HasSuggestion);
        RejectCommand = new RelayCommand(_ => _ = panel.RejectAsync(this), _ => !panel.IsBusy && HasSuggestion);
    }

    public string SpeakerId { get; }

    public string? DisplayName { get; }

    /// <summary>"Interlocutor 2": shown next to the name so the user knows who is who.</summary>
    public string DefaultLabel { get; }

    public string Label => DisplayName ?? DefaultLabel;

    public string? SuggestedName { get; }

    public bool HasSuggestion => SuggestedName is not null;

    public string SuggestionText => SuggestedName is null ? string.Empty : Loc.Format("Voices_SuggestionQuestion", SuggestedName);

    /// <summary>Name being typed. Empty clears the name ("Interlocutor N" again).</summary>
    public string NameDraft
    {
        get => _nameDraft;
        set => Set(ref _nameDraft, value ?? string.Empty);
    }

    public bool IsNameChanged => !string.Equals(NameDraft.Trim(), DisplayName ?? string.Empty, StringComparison.Ordinal);

    public ICommand SaveNameCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand RejectCommand { get; }
}

/// <summary>
/// "Interlocutores" panel of the meeting detail: optional names for the speakers, voice
/// suggestions from the saved voices (F11) and the "Reunión presencial" switch (F13).
/// Naming is optional: the panel never asks for it.
/// </summary>
internal sealed class SpeakersPanelViewModel : ObservableObject
{
    private readonly Func<bool> _isAiAvailable;
    private readonly Func<Task>? _reload;
    private string? _sessionId;
    private bool _isInPerson;
    private bool _isBusy;
    private string _statusText = string.Empty;
    private int _generation;

    /// <param name="reload">Reloads the whole detail (labels, transcript) after a change; the detail then reloads this panel.</param>
    public SpeakersPanelViewModel(IVoiceActions voices, Func<bool> isAiAvailable, Func<Task>? reload = null)
    {
        Voices = voices ?? throw new ArgumentNullException(nameof(voices));
        _isAiAvailable = isAiAvailable ?? throw new ArgumentNullException(nameof(isAiAvailable));
        _reload = reload;
    }

    public IVoiceActions Voices { get; }

    public ObservableCollection<SpeakerRowViewModel> Speakers { get; } = new();

    public bool HasSpeakers => Speakers.Count > 0;

    public bool HasSuggestions => Speakers.Any(speaker => speaker.HasSuggestion);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanChangeInPerson));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>In-person mode needs the diarization models of the AI pack.</summary>
    public bool CanChangeInPerson => _sessionId is not null && _isAiAvailable() && !IsBusy;

    /// <summary>"Reunión presencial". Changing it re-identifies the speakers (two-way bound).</summary>
    public bool IsInPerson
    {
        get => _isInPerson;
        set
        {
            if (_isInPerson != value && Set(ref _isInPerson, value))
            {
                _ = ApplyInPersonAsync(value);
            }
        }
    }

    public async Task LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        _sessionId = sessionId;
        SpeakerPanelData data;
        try
        {
            data = await Voices.LoadAsync(sessionId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            data = SpeakerPanelData.Empty;
        }

        if (generation != _generation)
        {
            return;
        }

        Speakers.Clear();
        foreach (var item in data.Speakers)
        {
            Speakers.Add(new SpeakerRowViewModel(item, this));
        }

        _isInPerson = data.InPerson;
        Raise(nameof(IsInPerson), nameof(HasSpeakers), nameof(HasSuggestions), nameof(CanChangeInPerson));
    }

    public Task RenameAsync(SpeakerRowViewModel row) =>
        RunAsync(id => Voices.RenameSpeakerAsync(id, row.SpeakerId, string.IsNullOrWhiteSpace(row.NameDraft) ? null : row.NameDraft.Trim(), CancellationToken.None));

    public Task ConfirmAsync(SpeakerRowViewModel row) =>
        RunAsync(id => Voices.ConfirmSuggestionAsync(id, row.SpeakerId, CancellationToken.None));

    public Task RejectAsync(SpeakerRowViewModel row) =>
        RunAsync(id => Voices.RejectSuggestionAsync(id, row.SpeakerId, CancellationToken.None));

    /// <summary>Reloads after "Voces guardadas" changed something.</summary>
    public Task ReloadAsync() => _sessionId is null ? Task.CompletedTask : LoadAsync(_sessionId, CancellationToken.None);

    private async Task ApplyInPersonAsync(bool inPerson)
    {
        StatusText = Loc.Get("Speakers_Identifying");
        var failed = await RunAsync(id => Voices.SetInPersonAsync(id, inPerson, CancellationToken.None)).ConfigureAwait(true);
        if (failed)
        {
            // Nothing changed on disk: show the switch as it really is.
            _isInPerson = !inPerson;
            Raise(nameof(IsInPerson));
        }
    }

    /// <summary>Runs one change, then reloads. Returns true when it failed.</summary>
    private async Task<bool> RunAsync(Func<string, Task> change)
    {
        if (_sessionId is not { } sessionId || IsBusy)
        {
            return true;
        }

        IsBusy = true;
        var failed = false;
        try
        {
            await change(sessionId).ConfigureAwait(true);
            StatusText = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failed = true;
            StatusText = exception is AiPackMissingException
                ? Loc.Get("Status_AiPackMissing")
                : Loc.Format("Voices_Failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (!failed)
        {
            await (_reload?.Invoke() ?? LoadAsync(sessionId, CancellationToken.None)).ConfigureAwait(true);
        }

        return failed;
    }
}
