using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

internal sealed class SavedVoiceRowViewModel : ObservableObject
{
    private string _nameDraft;

    public SavedVoiceRowViewModel(SavedVoiceItem voice, SavedVoicesViewModel owner)
    {
        Id = voice.Id;
        IsOwner = voice.IsOwner;
        Name = voice.Name;
        _nameDraft = voice.Name ?? string.Empty;
        Title = voice.IsOwner ? Loc.Get("Voices_OwnerVoice") : voice.Name ?? string.Empty;
        Detail = Loc.Format("Voices_Detail", voice.Meetings, UiFormat.Date(voice.LastSeen, "Format_MeetingDate"));
        RenameCommand = new RelayCommand(
            _ => _ = owner.RenameAsync(this),
            _ => !IsOwner && !owner.IsBusy && !string.IsNullOrWhiteSpace(NameDraft) &&
                 !string.Equals(NameDraft.Trim(), Name, StringComparison.Ordinal));
        ForgetCommand = new RelayCommand(_ => _ = owner.ForgetAsync(this), _ => !owner.IsBusy);
    }

    public string Id { get; }

    public bool IsOwner { get; }

    public bool IsNamed => !IsOwner;

    public string? Name { get; }

    public string Title { get; }

    /// <summary>"Oída en 3 reuniones · última: ...".</summary>
    public string Detail { get; }

    public string NameDraft
    {
        get => _nameDraft;
        set => Set(ref _nameDraft, value ?? string.Empty);
    }

    public ICommand RenameCommand { get; }

    public ICommand ForgetCommand { get; }
}

/// <summary>"Voces guardadas": list, rename and forget saved voices, and the auto-apply switch.</summary>
internal sealed class SavedVoicesViewModel : ObservableObject
{
    private readonly IVoiceActions _voices;
    private readonly IUiShell _shell;
    private bool _autoApply;
    private bool _isBusy;
    private string _statusText = string.Empty;

    public SavedVoicesViewModel(IVoiceActions voices, IUiShell shell)
    {
        _voices = voices ?? throw new ArgumentNullException(nameof(voices));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public ObservableCollection<SavedVoiceRowViewModel> Voices { get; } = new();

    public bool IsEmpty => Voices.Count == 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>"Aplicar automáticamente": confident matches are named without asking. Off by default.</summary>
    public bool AutoApply
    {
        get => _autoApply;
        set
        {
            if (Set(ref _autoApply, value))
            {
                _ = RunAsync(() => _voices.SetAutoApplyAsync(value, CancellationToken.None), reload: false);
            }
        }
    }

    public async Task LoadAsync()
    {
        SavedVoices saved;
        try
        {
            saved = await _voices.LoadVoicesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = Loc.Format("Voices_Failed", exception.Message);
            return;
        }

        Voices.Clear();
        foreach (var voice in saved.Voices)
        {
            Voices.Add(new SavedVoiceRowViewModel(voice, this));
        }

        _autoApply = saved.AutoApply;
        Raise(nameof(AutoApply), nameof(IsEmpty));
    }

    public Task RenameAsync(SavedVoiceRowViewModel voice) =>
        RunAsync(() => _voices.RenameVoiceAsync(voice.Id, voice.NameDraft.Trim(), CancellationToken.None));

    public Task ForgetAsync(SavedVoiceRowViewModel voice)
    {
        if (!_shell.ConfirmDestructive(Loc.Get("Voices_ForgetTitle"), Loc.Format("Voices_ForgetMessage", voice.Title)))
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _voices.ForgetVoiceAsync(voice.Id, CancellationToken.None));
    }

    private async Task RunAsync(Func<Task> change, bool reload = true)
    {
        IsBusy = true;
        try
        {
            await change().ConfigureAwait(true);
            StatusText = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = Loc.Format("Voices_Failed", exception.Message);
            reload = true;
        }
        finally
        {
            IsBusy = false;
        }

        if (reload)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }
}
