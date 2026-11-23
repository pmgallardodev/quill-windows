namespace Quill.Windows.Privacy;

internal enum NotificationKind
{
    Info,
    Warning,
    Error,
}

internal enum NotificationTopic
{
    General,

    /// <summary>A meeting app started using the microphone and Quill offers to record.</summary>
    MeetingDetected,
}

/// <param name="Subject">For <see cref="NotificationTopic.MeetingDetected"/>: the meeting application.</param>
internal sealed record QuillNotification(
    string Title,
    string Text,
    NotificationKind Kind,
    TimeSpan Duration,
    NotificationTopic Topic,
    string? Subject,
    DateTimeOffset At);

/// <summary>
/// The single route for user notifications. Outside discreet mode they become OS balloons; in
/// discreet mode a balloon could pop over a shared screen, so they are kept in-app instead:
/// <see cref="InAppNotification"/> is raised and <see cref="Recent"/> keeps the last few for a
/// panel that opens later. <see cref="MeetingDetected"/> is raised in both modes so a UI can
/// offer "record" without an OS popup; the recording hotkey keeps working either way.
/// Call from the UI thread.
/// </summary>
internal sealed class QuillNotifier
{
    public const int RecentCapacity = 20;

    private readonly Action<QuillNotification> _showOsNotification;
    private readonly Func<bool> _isDiscreet;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LinkedList<QuillNotification> _recent = new();

    public QuillNotifier(Action<QuillNotification> showOsNotification, Func<bool> isDiscreet, Func<DateTimeOffset>? clock = null)
    {
        _showOsNotification = showOsNotification ?? throw new ArgumentNullException(nameof(showOsNotification));
        _isDiscreet = isDiscreet ?? throw new ArgumentNullException(nameof(isDiscreet));
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>A notification that was kept in-app instead of shown by the OS (discreet mode).</summary>
    public event EventHandler<QuillNotification>? InAppNotification;

    /// <summary>A meeting app is using the microphone; raised whether or not a balloon was shown.</summary>
    public event EventHandler<QuillNotification>? MeetingDetected;

    /// <summary>Newest last; only notifications kept in-app.</summary>
    public IReadOnlyList<QuillNotification> Recent => [.. _recent];

    /// <returns>True when the OS showed it; false when it was kept in-app.</returns>
    public bool Notify(
        int timeoutMilliseconds,
        string title,
        string text,
        NotificationKind kind,
        NotificationTopic topic = NotificationTopic.General,
        string? subject = null)
    {
        var notification = new QuillNotification(
            title,
            text,
            kind,
            TimeSpan.FromMilliseconds(Math.Max(0, timeoutMilliseconds)),
            topic,
            subject,
            _clock());

        var shown = false;
        if (_isDiscreet())
        {
            _recent.AddLast(notification);
            while (_recent.Count > RecentCapacity)
            {
                _recent.RemoveFirst();
            }

            InAppNotification?.Invoke(this, notification);
        }
        else
        {
            _showOsNotification(notification);
            shown = true;
        }

        if (topic == NotificationTopic.MeetingDetected)
        {
            MeetingDetected?.Invoke(this, notification);
        }

        return shown;
    }

    public void ClearRecent() => _recent.Clear();
}
