using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

internal enum SummaryState
{
    Loading,

    /// <summary>The final summary (summary.json) is shown.</summary>
    Ready,

    /// <summary>Only the live summary exists (recording, or the final one was never generated).</summary>
    Live,

    Generating,
    Failed,

    /// <summary>No summary yet and the AI pack can make one.</summary>
    NoSummary,

    /// <summary>No summary and no AI pack to make one.</summary>
    AiPackMissing,
}

/// <summary>One bullet of a summary section. Action items are checkable.</summary>
internal sealed class SummaryItemViewModel : ObservableObject
{
    private readonly Action<SummaryItemViewModel, bool>? _onDoneChanged;
    private bool _isDone;

    public SummaryItemViewModel(
        string text,
        string detail,
        TimeSpan? at,
        int index = -1,
        bool isDone = false,
        Action<SummaryItemViewModel, bool>? onDoneChanged = null)
    {
        Text = text;
        Detail = detail;
        At = at;
        Index = index;
        _isDone = isDone;
        _onDoneChanged = onDoneChanged;
    }

    public string Text { get; }

    /// <summary>Owner, due date, decided by... Empty when there is nothing to add.</summary>
    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public TimeSpan? At { get; }

    public bool HasAt => At is not null;

    public TimeSpan AtValue => At ?? TimeSpan.Zero;

    /// <summary>Index into <see cref="MeetingSummary.ActionItems"/>; -1 for other sections.</summary>
    public int Index { get; }

    public bool IsCheckable => _onDoneChanged is not null;

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (Set(ref _isDone, value))
            {
                _onDoneChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>Restores the box without saving again (used when the save failed).</summary>
    public void Revert(bool isDone)
    {
        _isDone = isDone;
        Raise(nameof(IsDone));
    }
}

/// <summary>A section of the summary, in design §7.1 order. Empty sections render "Ninguno".</summary>
internal sealed record SummarySectionViewModel(string Title, IReadOnlyList<SummaryItemViewModel> Items)
{
    public bool IsEmpty => Items.Count == 0;

    public int Count => Items.Count;
}

/// <summary>
/// Summary tab. Shows the final summary, or the live one when that is all there is, and
/// offers generation (template + level) when the AI pack is installed. Ticking an action
/// item saves it back to the same summary file.
/// </summary>
internal sealed class SummaryViewModel : ObservableObject
{
    private readonly ISessionStore _store;
    private readonly ISessionActions _actions;
    private readonly IUiShell _shell;
    private string _sessionId = string.Empty;
    private string _meetingTitle = string.Empty;
    private SpeakerLabels _labels = SpeakerLabels.Empty;
    private MeetingSummary? _summary;
    private SummaryState _state = SummaryState.Loading;
    private string _executiveSummary = string.Empty;
    private string _errorText = string.Empty;
    private string _statusText = string.Empty;
    private string _selectedTemplate;
    private SummaryLevel _selectedLevel = SummaryLevel.Brief;
    private bool _canGenerate = true;
    private int _loadGeneration;
    private CancellationTokenSource? _generation;

    public SummaryViewModel(ISessionStore store, ISessionActions actions, IUiShell shell)
    {
        _store = store;
        _actions = actions;
        _shell = shell;
        _selectedTemplate = actions.SummaryTemplates.FirstOrDefault() ?? AiPackMissingSessionActions.DefaultTemplate;
        GenerateCommand = new RelayCommand(_ => _ = GenerateAsync(), _ => CanGenerateNow);
        CancelGenerationCommand = new RelayCommand(_ => _generation?.Cancel(), _ => _state == SummaryState.Generating);
        CopyMarkdownCommand = new RelayCommand(_ => Copy(markdown: true), _ => _summary is not null);
        CopyEmailCommand = new RelayCommand(_ => Copy(markdown: false), _ => _summary is not null);
        PlayAtCommand = new RelayCommand(PlayAt);
    }

    public ObservableCollection<SummarySectionViewModel> Sections { get; } = new();

    public ICommand GenerateCommand { get; }

    public ICommand CancelGenerationCommand { get; }

    public ICommand CopyMarkdownCommand { get; }

    public ICommand CopyEmailCommand { get; }

    /// <summary>Parameter: the cited <see cref="TimeSpan"/>.</summary>
    public ICommand PlayAtCommand { get; }

    public IReadOnlyList<string> Templates => _actions.SummaryTemplates;

    public IReadOnlyList<SummaryLevel> Levels { get; } = new[] { SummaryLevel.Brief, SummaryLevel.Detailed };

    public string SelectedTemplate
    {
        get => _selectedTemplate;
        set => Set(ref _selectedTemplate, value ?? AiPackMissingSessionActions.DefaultTemplate);
    }

    public SummaryLevel SelectedLevel
    {
        get => _selectedLevel;
        set => Set(ref _selectedLevel, value);
    }

    public bool IsAiAvailable => _actions.IsAiAvailable;

    public SummaryState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(
                    nameof(IsLoading), nameof(HasSummary), nameof(IsLive), nameof(IsGenerating), nameof(IsFailed),
                    nameof(IsNoSummary), nameof(IsAiPackMissing), nameof(CanGenerateNow), nameof(ShowGenerateBar));
            }
        }
    }

    public bool IsLoading => _state == SummaryState.Loading;

    public bool HasSummary => _state is SummaryState.Ready or SummaryState.Live;

    public bool IsLive => _state == SummaryState.Live;

    public bool IsGenerating => _state == SummaryState.Generating;

    public bool IsFailed => _state == SummaryState.Failed;

    public bool IsNoSummary => _state == SummaryState.NoSummary;

    public bool IsAiPackMissing => _state == SummaryState.AiPackMissing;

    /// <summary>False while recording or transcribing: the final summary needs the final transcript.</summary>
    public bool CanGenerate
    {
        get => _canGenerate;
        set
        {
            if (Set(ref _canGenerate, value))
            {
                Raise(nameof(CanGenerateNow), nameof(ShowGenerateBar));
            }
        }
    }

    public bool CanGenerateNow =>
        IsAiAvailable && _canGenerate && _sessionId.Length > 0 &&
        _state is SummaryState.Ready or SummaryState.Live or SummaryState.Failed or SummaryState.NoSummary;

    /// <summary>Template and level selector plus the (re)generate button.</summary>
    public bool ShowGenerateBar => IsAiAvailable && _state != SummaryState.Loading;

    public string ExecutiveSummary
    {
        get => _executiveSummary;
        private set
        {
            if (Set(ref _executiveSummary, value))
            {
                Raise(nameof(HasExecutiveSummary));
            }
        }
    }

    public bool HasExecutiveSummary => _executiveSummary.Length > 0;

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    /// <summary>Result of the last copy or save.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>Loaded summary, for tests and for the detail header.</summary>
    public MeetingSummary? Summary => _summary;

    public async Task LoadAsync(string sessionId, string meetingTitle, SpeakerLabels labels, CancellationToken cancellationToken)
    {
        _sessionId = sessionId;
        _meetingTitle = meetingTitle;
        _labels = labels;
        var generation = ++_loadGeneration;
        _generation?.Cancel();
        StatusText = string.Empty;
        ErrorText = string.Empty;
        State = SummaryState.Loading;

        MeetingSummary? summary;
        try
        {
            summary = await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(true)
                ?? await _store.LoadSummaryAsync(sessionId, SummaryKind.Live, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == _loadGeneration)
            {
                Show(null);
                ErrorText = Loc.Get("Summary_LoadFailed");
            }

            return;
        }

        if (generation == _loadGeneration && !cancellationToken.IsCancellationRequested)
        {
            Show(summary);
        }
    }

    /// <summary>Keeps the title used by the copied text in sync with a rename.</summary>
    public void SetMeetingTitle(string title) => _meetingTitle = title;

    public async Task GenerateAsync()
    {
        if (!CanGenerateNow)
        {
            return;
        }

        var sessionId = _sessionId;
        var generation = _loadGeneration;
        using var cancellation = new CancellationTokenSource();
        _generation = cancellation;
        ErrorText = string.Empty;
        State = SummaryState.Generating;
        try
        {
            await _actions.GenerateSummaryAsync(sessionId, _selectedTemplate, _selectedLevel, cancellation.Token)
                .ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                return;
            }

            var summary = await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, CancellationToken.None)
                .ConfigureAwait(true);
            Show(summary);
        }
        catch (AiPackMissingException)
        {
            if (generation == _loadGeneration)
            {
                Show(_summary);
                State = _summary is null ? SummaryState.AiPackMissing : State;
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _loadGeneration)
            {
                Show(_summary);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _loadGeneration)
            {
                ErrorText = Loc.Format("Summary_GenerateFailed", exception.Message);
                State = SummaryState.Failed;
            }
        }
        finally
        {
            if (ReferenceEquals(_generation, cancellation))
            {
                _generation = null;
            }
        }
    }

    private void Show(MeetingSummary? summary)
    {
        _summary = summary;
        Sections.Clear();
        if (summary is null)
        {
            ExecutiveSummary = string.Empty;
            State = IsAiAvailable ? SummaryState.NoSummary : SummaryState.AiPackMissing;
            return;
        }

        ExecutiveSummary = summary.ExecutiveSummary?.Trim() ?? string.Empty;
        foreach (var section in BuildSections(summary, _labels, OnActionItemDone))
        {
            Sections.Add(section);
        }

        State = summary.Meta.Kind == SummaryKind.Live ? SummaryState.Live : SummaryState.Ready;
    }

    /// <summary>Every section after the executive summary, in design §7.1 order.</summary>
    public static IReadOnlyList<SummarySectionViewModel> BuildSections(
        MeetingSummary summary,
        SpeakerLabels labels,
        Action<SummaryItemViewModel, bool>? onActionItemDone)
    {
        string Who(string? speakerId) => string.IsNullOrWhiteSpace(speakerId) ? string.Empty : labels.Label(speakerId);

        return new[]
        {
            new SummarySectionViewModel(
                Loc.Get("Summary_Decisions"),
                summary.Decisions.Select(decision => new SummaryItemViewModel(decision.Text.Trim(), Who(decision.DecidedBy), decision.At)).ToList()),
            new SummarySectionViewModel(
                Loc.Get("Summary_ActionItems"),
                summary.ActionItems.Select((item, index) => new SummaryItemViewModel(
                    item.Text.Trim(),
                    string.Join(" · ", new[]
                    {
                        Who(item.Owner),
                        string.IsNullOrWhiteSpace(item.Due) ? string.Empty : Loc.Format("Summary_Due", item.Due.Trim()),
                    }.Where(part => part.Length > 0)),
                    item.At,
                    index,
                    item.Done,
                    onActionItemDone)).ToList()),
            new SummarySectionViewModel(
                Loc.Get("Summary_OpenQuestions"),
                summary.OpenQuestions.Select(question => new SummaryItemViewModel(question.Text.Trim(), string.Empty, question.At)).ToList()),
            new SummarySectionViewModel(
                Loc.Get("Summary_Topics"),
                summary.Topics.Select(topic => new SummaryItemViewModel(topic.Title.Trim(), string.Empty, topic.Start)).ToList()),
            new SummarySectionViewModel(
                Loc.Get("Summary_Risks"),
                summary.Risks.Select(risk => new SummaryItemViewModel(risk.Text.Trim(), string.Empty, risk.At)).ToList()),
            new SummarySectionViewModel(
                Loc.Get("Summary_KeyFacts"),
                summary.KeyFacts.Select(fact => new SummaryItemViewModel(fact.Text.Trim(), string.Empty, fact.At)).ToList()),
            new SummarySectionViewModel(
                Loc.Get("Summary_NextSteps"),
                summary.NextSteps.Select(step => new SummaryItemViewModel(step.Trim(), string.Empty, null)).ToList()),
        };
    }

    private async void OnActionItemDone(SummaryItemViewModel item, bool done)
    {
        var summary = _summary;
        if (summary is null || item.Index < 0 || item.Index >= summary.ActionItems.Count)
        {
            return;
        }

        var updated = ActionItemRules.SetDone(summary, item.Index, done);
        _summary = updated;
        try
        {
            await _store.SaveSummaryAsync(_sessionId, updated, CancellationToken.None).ConfigureAwait(true);
            StatusText = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (ReferenceEquals(_summary, updated))
            {
                _summary = summary;
            }

            item.Revert(!done);
            StatusText = Loc.Get("Summary_SaveFailed");
        }
    }

    private void Copy(bool markdown)
    {
        if (_summary is not { } summary)
        {
            return;
        }

        var text = markdown
            ? SummaryFormatter.ToMarkdown(summary, _meetingTitle, _labels.Label)
            : SummaryFormatter.ToEmail(summary, _meetingTitle, _labels.Label);
        StatusText = _shell.TryCopyText(text) ? Loc.Get("Status_Copied") : Loc.Get("Status_ClipboardBusy");
    }

    private void PlayAt(object? parameter)
    {
        if (parameter is TimeSpan at && _sessionId.Length > 0)
        {
            _actions.PlayAt(_sessionId, at);
        }
    }
}
