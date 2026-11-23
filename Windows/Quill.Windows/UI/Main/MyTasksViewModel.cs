using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>One of my action items, with the meeting it came from.</summary>
internal sealed class MyTaskViewModel : ObservableObject
{
    private readonly Func<MyTaskViewModel, bool, Task<bool>> _onDoneChanged;
    private bool _isDone;

    public MyTaskViewModel(
        string sessionId,
        string meetingTitle,
        string meetingDate,
        SummaryKind kind,
        int index,
        ActionItem item,
        Func<MyTaskViewModel, bool, Task<bool>> onDoneChanged)
    {
        SessionId = sessionId;
        MeetingTitle = meetingTitle;
        MeetingDate = meetingDate;
        Kind = kind;
        Index = index;
        Text = item.Text.Trim();
        Due = string.IsNullOrWhiteSpace(item.Due) ? string.Empty : Loc.Format("Summary_Due", item.Due.Trim());
        At = item.At;
        _isDone = item.Done;
        _onDoneChanged = onDoneChanged;
    }

    public string SessionId { get; }

    public string MeetingTitle { get; }

    public string MeetingDate { get; }

    public SummaryKind Kind { get; }

    public int Index { get; }

    public string Text { get; }

    public string Due { get; }

    public bool HasDue => Due.Length > 0;

    public TimeSpan At { get; }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (Set(ref _isDone, value))
            {
                _ = SaveAsync(value);
            }
        }
    }

    private async Task SaveAsync(bool done)
    {
        if (!await _onDoneChanged(this, done).ConfigureAwait(true))
        {
            _isDone = !done;
            Raise(nameof(IsDone));
        }
    }
}

/// <summary>
/// "Mis tareas": my action items across every meeting summary, pending first. Ticking one
/// saves it to that meeting's summary file.
/// </summary>
internal sealed class MyTasksViewModel : ObservableObject
{
    private readonly ISessionStore _store;
    private readonly Action<string> _openMeeting;
    private string _status = string.Empty;
    private bool _isLoading;
    private int _generation;

    public MyTasksViewModel(ISessionStore store, Action<string> openMeeting)
    {
        _store = store;
        _openMeeting = openMeeting;
        OpenMeetingCommand = new RelayCommand(parameter =>
        {
            if (parameter is MyTaskViewModel task)
            {
                _openMeeting(task.SessionId);
            }
        });
    }

    public ObservableCollection<MyTaskViewModel> Pending { get; } = new();

    public ObservableCollection<MyTaskViewModel> Done { get; } = new();

    public ICommand OpenMeetingCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool IsEmpty => !_isLoading && Pending.Count == 0 && Done.Count == 0;

    public bool HasDone => Done.Count > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Reads the summaries of <paramref name="meetings"/> (newest first) that have one.</summary>
    public async Task LoadAsync(IReadOnlyList<MeetingRowViewModel> meetings, CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        IsLoading = true;
        Status = string.Empty;
        var tasks = new List<MyTaskViewModel>();
        try
        {
            foreach (var meeting in meetings.Where(meeting => meeting.HasSummary))
            {
                var summary = await LoadSummaryAsync(meeting.SessionId, cancellationToken).ConfigureAwait(true);
                if (summary is null)
                {
                    continue;
                }

                foreach (var index in ActionItemRules.MineIndexes(summary))
                {
                    tasks.Add(new MyTaskViewModel(
                        meeting.SessionId,
                        meeting.DisplayName,
                        meeting.DayGroup,
                        summary.Meta.Kind,
                        index,
                        summary.ActionItems[index],
                        SetDoneAsync));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }

        if (generation != _generation)
        {
            return;
        }

        Pending.Clear();
        Done.Clear();
        foreach (var task in tasks)
        {
            (task.IsDone ? Done : Pending).Add(task);
        }

        Raise(nameof(IsEmpty), nameof(HasDone));
    }

    private async Task<MeetingSummary?> LoadSummaryAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(true)
                ?? await _store.LoadSummaryAsync(sessionId, SummaryKind.Live, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads the summary before writing so a change made elsewhere (the Summary tab, a new
    /// generation) is not overwritten with this list's older copy.
    /// </summary>
    private async Task<bool> SetDoneAsync(MyTaskViewModel task, bool done)
    {
        try
        {
            var summary = await _store.LoadSummaryAsync(task.SessionId, task.Kind, CancellationToken.None).ConfigureAwait(true);
            if (summary is null || task.Index >= summary.ActionItems.Count ||
                !string.Equals(summary.ActionItems[task.Index].Text.Trim(), task.Text, StringComparison.Ordinal))
            {
                Status = Loc.Get("MyTasks_Changed");
                return false;
            }

            await _store.SaveSummaryAsync(task.SessionId, ActionItemRules.SetDone(summary, task.Index, done), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = Loc.Get("Summary_SaveFailed");
            return false;
        }

        Status = string.Empty;
        var (from, to) = done ? (Pending, Done) : (Done, Pending);
        if (from.Remove(task))
        {
            to.Insert(0, task);
        }

        Raise(nameof(IsEmpty), nameof(HasDone));
        return true;
    }
}
